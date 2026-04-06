param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$Organization,
    [Parameter(Mandatory)][string]$CertificatePath
)

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelperRest.psm1') -Force

# Configure storage auth from environment
$script:StorageAuthMethod = $env:STORAGE_AUTH_METHOD ?? 'managed_identity'
if ($script:StorageAuthMethod -eq 'service_principal') {
    $spCertPath = Get-CertificateFromKeyVault -VaultName $env:KEYVAULT_NAME -CertName $env:STORAGE_SP_CERT_NAME
    $script:StorageCertBytes = [System.IO.File]::ReadAllBytes($spCertPath)
    $script:StorageSpTenantId = $env:STORAGE_SP_TENANT_ID
    $script:StorageSpClientId = $env:STORAGE_SP_CLIENT_ID
    Remove-CertificateFile -Path $spCertPath
}

# Set upload function reference for Invoke-Ingestion.ps1
$script:UploadFunction = ${function:Write-ToAdlsRest}

# Load certificate and connect to Exchange Online
$certBytes = [System.IO.File]::ReadAllBytes($CertificatePath)
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)

$exoParams = @{
    Certificate  = $cert
    AppId        = $ClientId
    Organization = $Organization
    ShowBanner   = $false
}
Connect-ExchangeOnline @exoParams

Write-Log "Connected to Exchange Online for tenant '$($env:TENANT_KEY)'" -TenantKey $env:TENANT_KEY

# Store auth config for RunspacePool reconnection
$script:AuthConfig = @{
    ClientId     = $ClientId
    Organization = $Organization
}
$script:CertBytes = $certBytes
