param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$Organization,
    [Parameter(Mandatory)][string]$CertificatePath
)

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelperRest.psm1') -Force

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

Write-Log "EXO Connect: AppId=$ClientId, Org=$Organization, CertThumbprint=$($cert.Thumbprint)" -TenantKey $env:TENANT_KEY
Connect-ExchangeOnline @exoParams

# Diagnostic: verify connection context
try {
    $ctx = Get-ConnectionInformation
    Write-Log "EXO Context: Org=$($ctx.Organization), UserPrincipalName=$($ctx.UserPrincipalName), ConnectionId=$($ctx.ConnectionId), TokenStatus=$($ctx.TokenStatus)" -TenantKey $env:TENANT_KEY
} catch {
    Write-Log "EXO Context check failed: $($_.Exception.Message)" -Level WARN -TenantKey $env:TENANT_KEY
}

Write-Log "Connected to Exchange Online for tenant '$($env:TENANT_KEY)'" -TenantKey $env:TENANT_KEY

# Store auth config for RunspacePool reconnection
$script:AuthConfig = @{
    ClientId     = $ClientId
    Organization = $Organization
}
$script:CertBytes = $certBytes
