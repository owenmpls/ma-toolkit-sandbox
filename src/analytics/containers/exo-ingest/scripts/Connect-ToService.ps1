param(
    [Parameter(Mandatory)][psobject]$TenantConfig,
    [Parameter(Mandatory)][string]$CertificatePath
)

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelperRest.psm1') -Force

# Set upload function reference for Invoke-Ingestion.ps1
$script:UploadFunction = ${function:Write-ToAdlsRest}

# Load certificate and connect to Exchange Online
$certBytes = [System.IO.File]::ReadAllBytes($CertificatePath)
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
)

$exoParams = @{
    Certificate  = $cert
    AppId        = $TenantConfig.client_id
    Organization = $TenantConfig.organization
    ShowBanner   = $false
}
Connect-ExchangeOnline @exoParams

Write-Log "Connected to Exchange Online for tenant '$($TenantConfig.tenant_key)'" -TenantKey $TenantConfig.tenant_key

# Store auth config for RunspacePool reconnection
$script:AuthConfig = @{
    ClientId     = $TenantConfig.client_id
    Organization = $TenantConfig.organization
}
$script:CertBytes = $certBytes
