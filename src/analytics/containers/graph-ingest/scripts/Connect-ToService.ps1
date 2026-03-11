param(
    [Parameter(Mandatory)][psobject]$TenantConfig,
    [Parameter(Mandatory)][string]$CertificatePath
)

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelper.psm1') -Force

# Set upload function reference for Invoke-Ingestion.ps1
$script:UploadFunction = ${function:Write-ToAdls}

# Load certificate and connect to Microsoft Graph
$certBytes = [System.IO.File]::ReadAllBytes($CertificatePath)
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)

Connect-MgGraph -Certificate $cert `
    -ClientId $TenantConfig.client_id `
    -TenantId $TenantConfig.tenant_id `
    -NoWelcome

Write-Log "Connected to Microsoft Graph for tenant '$($TenantConfig.tenant_key)'" -TenantKey $TenantConfig.tenant_key

# Store auth config for RunspacePool reconnection
$script:AuthConfig = @{
    ClientId = $TenantConfig.client_id
    TenantId = $TenantConfig.tenant_id
}
$script:CertBytes = $certBytes
