param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$CertificatePath
)

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelperRest.psm1') -Force

# Set upload function reference for Invoke-Ingestion.ps1
$script:UploadFunction = ${function:Write-ToAdlsRest}

# Load certificate and connect to Microsoft Graph
$certBytes = [System.IO.File]::ReadAllBytes($CertificatePath)
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)

Connect-MgGraph -Certificate $cert `
    -ClientId $ClientId `
    -TenantId $TenantId `
    -NoWelcome

Write-Log "Connected to Microsoft Graph for tenant '$($env:TENANT_KEY)'" -TenantKey $env:TENANT_KEY

# Store auth config for RunspacePool reconnection
$script:AuthConfig = @{
    ClientId = $ClientId
    TenantId = $TenantId
}
$script:CertBytes = $certBytes
