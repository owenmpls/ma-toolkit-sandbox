param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$Organization,
    [Parameter(Mandatory)][string]$CertificatePath
)

# Import Az.Storage early — its .NET assemblies must be loaded before
# Connect-ExchangeOnline to avoid REST client conflicts.
Import-Module Az.Storage -ErrorAction SilentlyContinue

Import-Module (Join-Path $PSScriptRoot 'modules/StorageHelperRest.psm1') -Force

# Set upload function reference for Invoke-Ingestion.ps1
$script:UploadFunction = ${function:Write-ToAdlsRest}

# Load certificate and connect to Exchange Online
$certBytes = [System.IO.File]::ReadAllBytes($CertificatePath)
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)

# Disconnect Azure context before connecting to EXO — the managed identity
# context from Connect-AzAccount can interfere with EXO's certificate-based
# token acquisition, causing 401 on REST API calls.
Disconnect-AzAccount -ErrorAction SilentlyContinue | Out-Null

$exoParams = @{
    Certificate  = $cert
    AppId        = $ClientId
    Organization = $Organization
    ShowBanner   = $false
}
Connect-ExchangeOnline @exoParams

# Reconnect to Azure (needed for Key Vault cert loading in Phase 2 and storage uploads)
Connect-AzAccount -Identity -WarningAction SilentlyContinue | Out-Null

# Diagnostic: verify cmdlets are available
$exoCmds = Get-Command -Module ExchangeOnlineManagement -ErrorAction SilentlyContinue
$exoMailboxCmd = Get-Command Get-EXOMailbox -ErrorAction SilentlyContinue
$distGroupCmd = Get-Command Get-DistributionGroup -ErrorAction SilentlyContinue
[Console]::Error.WriteLine("EXO_DIAG: Module commands=$($exoCmds.Count), Get-EXOMailbox=$(if($exoMailboxCmd){'found'}else{'NOT FOUND'}), Get-DistributionGroup=$(if($distGroupCmd){'found'}else{'NOT FOUND'})")
[Console]::Error.WriteLine("EXO_DIAG: Loaded modules=$(Get-Module | Select-Object -ExpandProperty Name | Where-Object { $_ -like '*Exchange*' -or $_ -like '*tmp*' })")

Write-Log "Connected to Exchange Online for tenant '$($env:TENANT_KEY)'" -TenantKey $env:TENANT_KEY

# Store auth config for RunspacePool reconnection
$script:AuthConfig = @{
    ClientId     = $ClientId
    Organization = $Organization
}
$script:CertBytes = $certBytes
