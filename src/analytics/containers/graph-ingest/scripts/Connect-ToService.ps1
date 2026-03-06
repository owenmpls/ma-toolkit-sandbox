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
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
)

Connect-MgGraph -Certificate $cert `
    -ClientId $TenantConfig.client_id `
    -TenantId $TenantConfig.tenant_id `
    -NoWelcome

# Log auth context for diagnostics
$ctx = Get-MgContext
Write-Log "Connected to Microsoft Graph for tenant '$($TenantConfig.tenant_key)' (AppId=$($ctx.ClientId), TenantId=$($ctx.TenantId), AuthType=$($ctx.AuthType), Scopes=$($ctx.Scopes -join ','))" -TenantKey $TenantConfig.tenant_key

# Sanity check: verify we can read directory data
try {
    $me = Invoke-MgGraphRequest -Method GET -Uri '/v1.0/organization' -ErrorAction Stop
    Write-Log "Graph sanity check passed: org=$($me.value[0].displayName)" -TenantKey $TenantConfig.tenant_key
}
catch {
    Write-Log "Graph sanity check FAILED: $($_.Exception.Message)" -Level ERROR -TenantKey $TenantConfig.tenant_key
    throw
}

# Store auth config for RunspacePool reconnection
$script:AuthConfig = @{
    ClientId = $TenantConfig.client_id
    TenantId = $TenantConfig.tenant_id
}
$script:CertBytes = $certBytes
