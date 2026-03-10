param(
    [Parameter(Mandatory)][psobject]$TenantConfig,
    [Parameter(Mandatory)][string]$CertificatePath
)

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelper.psm1') -Force

# Set upload function reference for Invoke-Ingestion.ps1
$script:UploadFunction = ${function:Write-ToAdls}

# Load certificate bytes and export as Base64 for PnP
$certBytes = [System.IO.File]::ReadAllBytes($CertificatePath)
$x509 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
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

# Store auth config for entity module reconnection (PnP needs per-site connections).
# Uses $global: because entity modules have their own module scope and cannot
# access $script: variables from the dot-sourced caller.
$global:AuthConfig = @{
    ClientId          = $TenantConfig.client_id
    TenantDomain      = $TenantConfig.organization
    CertificateBase64 = $certBase64
    AdminUrl          = $TenantConfig.admin_url
}
$global:CertBytes = $certBytes
