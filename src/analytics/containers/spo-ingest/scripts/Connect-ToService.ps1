param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$Organization,
    [Parameter(Mandatory)][string]$AdminUrl,
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

# Load certificate bytes and export as Base64 for PnP
$certBytes = [System.IO.File]::ReadAllBytes($CertificatePath)
$x509 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)
$pfxBytes = $x509.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx
)
$certBase64 = [System.Convert]::ToBase64String($pfxBytes)

# Connect to SharePoint Online admin site
Connect-PnPOnline -Url $AdminUrl `
    -ClientId $ClientId `
    -Tenant $Organization `
    -CertificateBase64Encoded $certBase64

Write-Log "Connected to SharePoint Online for tenant '$($env:TENANT_KEY)'" -TenantKey $env:TENANT_KEY

# Store auth config for RunspacePool reconnection in Phase 2.
$script:AuthConfig = @{
    ClientId          = $ClientId
    TenantDomain      = $Organization
    CertificateBase64 = $certBase64
    AdminUrl          = $AdminUrl
}
$script:CertBytes = $certBytes
