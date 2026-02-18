<#
.SYNOPSIS
    Authentication management for the Hybrid Worker.
.DESCRIPTION
    Handles service principal certificate auth for Azure resources,
    Key Vault certificate retrieval for target tenant auth,
    and on-premises credential retrieval from Key Vault.
#>

function Connect-HybridWorkerAzure {
    <#
    .SYNOPSIS
        Connects to Azure using service principal certificate authentication.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    Write-WorkerLog -Message 'Connecting to Azure with service principal certificate...'

    # Verify certificate exists in store
    $cert = Get-Item "Cert:\LocalMachine\My\$($Config.AuthCertificateThumbprint)" -ErrorAction SilentlyContinue
    if (-not $cert) {
        throw "Certificate with thumbprint '$($Config.AuthCertificateThumbprint)' not found in Cert:\LocalMachine\My"
    }

    Connect-AzAccount -ServicePrincipal `
        -CertificateThumbprint $Config.AuthCertificateThumbprint `
        -ApplicationId $Config.AuthAppId `
        -Tenant $Config.AuthTenantId `
        -ErrorAction Stop | Out-Null

    Write-WorkerLog -Message "Connected to Azure as SP '$($Config.AuthAppId)' in tenant '$($Config.AuthTenantId)'."
}

function Get-ServiceBusCredential {
    <#
    .SYNOPSIS
        Creates a ClientCertificateCredential for Service Bus authentication.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    $cert = Get-Item "Cert:\LocalMachine\My\$($Config.AuthCertificateThumbprint)"
    $credential = [Azure.Identity.ClientCertificateCredential]::new(
        $Config.AuthTenantId,
        $Config.AuthAppId,
        $cert
    )
    return $credential
}

function Get-WorkerCertificate {
    <#
    .SYNOPSIS
        Retrieves a certificate (with private key) from Azure Key Vault.
    .DESCRIPTION
        Key Vault Certificates store the PFX as an associated secret (same name).
        Get-AzKeyVaultSecret returns the base64-encoded PFX which we convert to
        an X509Certificate2 with EphemeralKeySet (avoids writing keys to disk on Linux).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$KeyVaultName,

        [Parameter(Mandatory)]
        [string]$CertificateName
    )

    Write-WorkerLog -Message "Retrieving certificate '$CertificateName' from Key Vault '$KeyVaultName'..."

    try {
        $secret = Get-AzKeyVaultSecret -VaultName $KeyVaultName -Name $CertificateName -ErrorAction Stop
        $base64 = $secret.SecretValue | ConvertFrom-SecureString -AsPlainText
        $pfxBytes = [Convert]::FromBase64String($base64)
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $pfxBytes,
            [string]::Empty,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        )
        Write-WorkerLog -Message "Successfully retrieved certificate '$CertificateName' (Thumbprint: $($cert.Thumbprint))."
        return $cert
    }
    catch {
        throw "Failed to retrieve certificate '$CertificateName' from Key Vault '$KeyVaultName': $($_.Exception.Message)"
    }
}

function Initialize-RunspaceAuth {
    <#
    .SYNOPSIS
        Initializes MgGraph and ExchangeOnline connections within a runspace.
    .DESCRIPTION
        Called during runspace initialization to establish authenticated sessions
        using certificate-based authentication.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TenantId,

        [Parameter(Mandatory)]
        [string]$AppId,

        [Parameter(Mandatory)]
        [string]$Organization,

        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,

        [Parameter(Mandatory)]
        [int]$RunspaceIndex
    )

    Write-WorkerLog -Message "Initializing auth for runspace $RunspaceIndex..." -Properties @{ RunspaceIndex = $RunspaceIndex }

    # Connect to Microsoft Graph
    try {
        Connect-MgGraph -ClientId $AppId -TenantId $TenantId -Certificate $Certificate -NoWelcome -ErrorAction Stop
        Write-WorkerLog -Message "Runspace ${RunspaceIndex}: MgGraph connected." -Properties @{ RunspaceIndex = $RunspaceIndex }
    }
    catch {
        throw "Runspace ${RunspaceIndex}: Failed to connect MgGraph: $($_.Exception.Message)"
    }

    # Connect to Exchange Online (Organization requires tenant domain name, not GUID)
    try {
        $connectParams = @{
            Certificate = $Certificate
            AppId       = $AppId
            Organization = $Organization
            ShowBanner   = $false
            ErrorAction  = 'Stop'
        }
        Connect-ExchangeOnline @connectParams
        Write-WorkerLog -Message "Runspace ${RunspaceIndex}: Exchange Online connected." -Properties @{ RunspaceIndex = $RunspaceIndex }
    }
    catch {
        throw "Runspace ${RunspaceIndex}: Failed to connect Exchange Online: $($_.Exception.Message)"
    }
}

function Get-RunspaceAuthScriptBlock {
    <#
    .SYNOPSIS
        Returns a scriptblock that initializes auth sessions inside a runspace.
    .DESCRIPTION
        Uses certificate bytes (PFX) instead of X509Certificate2 because byte arrays
        serialize cleanly across runspace boundaries, while X509Certificate2 holds a
        private key handle that may not survive cross-runspace transfer reliably.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TenantId,

        [Parameter(Mandatory)]
        [string]$AppId,

        [Parameter(Mandatory)]
        [string]$Organization,

        [Parameter(Mandatory)]
        [byte[]]$CertificateBytes
    )

    $authScript = {
        param($TenantId, $AppId, $Organization, $CertificateBytes, $RunspaceIndex)

        # Reconstruct certificate from PFX bytes (EphemeralKeySet avoids writing keys to disk)
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $CertificateBytes,
            [string]::Empty,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        )

        # Connect MgGraph with certificate
        Connect-MgGraph -ClientId $AppId -TenantId $TenantId -Certificate $cert -NoWelcome -ErrorAction Stop

        # Connect Exchange Online with certificate (Organization requires tenant domain, not GUID)
        $exoParams = @{
            Certificate = $cert
            AppId       = $AppId
            Organization = $Organization
            ShowBanner   = $false
            ErrorAction  = 'Stop'
        }
        Connect-ExchangeOnline @exoParams

        # Store auth config for reactive reconnection on auth errors
        $global:WorkerAuthConfig = @{
            TenantId     = $TenantId
            AppId        = $AppId
            Organization = $Organization
        }
        $global:WorkerCertBytes = $CertificateBytes
    }

    return $authScript
}

function Get-OnPremCredential {
    <#
    .SYNOPSIS
        Retrieves a username/password credential from Key Vault for on-prem service auth.
    .DESCRIPTION
        The KV secret stores a JSON object: { "username": "domain\\user", "password": "..." }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$KeyVaultName,

        [Parameter(Mandatory)]
        [string]$SecretName
    )

    Write-WorkerLog -Message "Retrieving on-prem credential '$SecretName' from Key Vault..."

    $secret = Get-AzKeyVaultSecret -VaultName $KeyVaultName -Name $SecretName -ErrorAction Stop
    $secretText = $secret.SecretValue | ConvertFrom-SecureString -AsPlainText
    $credData = $secretText | ConvertFrom-Json

    $securePassword = ConvertTo-SecureString $credData.password -AsPlainText -Force
    $credential = [System.Management.Automation.PSCredential]::new($credData.username, $securePassword)

    Write-WorkerLog -Message "Retrieved credential for '$($credData.username)'."
    return $credential
}
