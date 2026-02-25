<#
.SYNOPSIS
    Authentication management for the Hybrid Worker.
.DESCRIPTION
    Handles service principal certificate auth for Azure resources
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

function Get-ForestCredentials {
    <#
    .SYNOPSIS
        Retrieves credentials for all configured AD forests from Key Vault.
    .DESCRIPTION
        Iterates through forest configurations and calls Get-OnPremCredential
        for each forest's credentialSecret. Returns a hashtable mapping
        forest name to PSCredential.
    .PARAMETER KeyVaultName
        The Key Vault name to retrieve credentials from.
    .PARAMETER Forests
        Array of forest config objects (must have name and credentialSecret properties).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$KeyVaultName,

        [Parameter(Mandatory)]
        [array]$Forests
    )

    $credentials = @{}
    foreach ($forest in $Forests) {
        Write-WorkerLog -Message "Retrieving credential for forest '$($forest.name)'..."
        $credentials[$forest.name] = Get-OnPremCredential -KeyVaultName $KeyVaultName -SecretName $forest.credentialSecret
    }

    Write-WorkerLog -Message "Retrieved credentials for $($credentials.Count) forest(s)."
    return $credentials
}
