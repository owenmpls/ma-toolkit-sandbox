<#
.SYNOPSIS
    Authentication management for the PowerShell Cloud Worker.
.DESCRIPTION
    Handles Azure managed identity login, Key Vault certificate retrieval,
    Microsoft Graph, and Exchange Online authentication using certificates.
#>

function Connect-WorkerAzure {
    <#
    .SYNOPSIS
        Connects to Azure using managed identity for Key Vault access.
    #>
    [CmdletBinding()]
    param()

    Write-WorkerLog -Message 'Connecting to Azure with managed identity...'

    try {
        # In Azure Container Apps, system-assigned managed identity is available automatically.
        # For local development, falls back to environment variables (AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET).
        Connect-AzAccount -Identity -ErrorAction Stop | Out-Null
        Write-WorkerLog -Message 'Successfully connected to Azure with managed identity.'
    }
    catch {
        Write-WorkerLog -Message "Managed identity auth failed, attempting environment credential: $($_.Exception.Message)" -Severity Warning
        try {
            if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET) {
                $secureSecret = ConvertTo-SecureString $env:AZURE_CLIENT_SECRET -AsPlainText -Force
                $credential = [System.Management.Automation.PSCredential]::new($env:AZURE_CLIENT_ID, $secureSecret)
                Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $env:AZURE_TENANT_ID -ErrorAction Stop | Out-Null
                Write-WorkerLog -Message 'Successfully connected to Azure with service principal credentials.'
            }
            else {
                throw 'No environment credentials available (AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET).'
            }
        }
        catch {
            throw "Failed to connect to Azure: $($_.Exception.Message)"
        }
    }
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
