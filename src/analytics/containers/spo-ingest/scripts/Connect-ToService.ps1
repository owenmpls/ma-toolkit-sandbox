param(
    [Parameter(Mandatory)][psobject]$TenantConfig,
    [Parameter(Mandatory)][string]$CertificatePath
)

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelperRest.psm1') -Force

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
Connect-PnPOnline -Url $TenantConfig.admin_url `
    -ClientId $TenantConfig.client_id `
    -Tenant $TenantConfig.organization `
    -CertificateBase64Encoded $certBase64

Write-Log "Connected to SharePoint Online for tenant '$($TenantConfig.tenant_key)'" -TenantKey $TenantConfig.tenant_key

# Store auth config for RunspacePool reconnection in Phase 2.
$script:AuthConfig = @{
    ClientId          = $TenantConfig.client_id
    TenantDomain      = $TenantConfig.organization
    CertificateBase64 = $certBase64
    AdminUrl          = $TenantConfig.admin_url
}
$script:CertBytes = $certBytes
