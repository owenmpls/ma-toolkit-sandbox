<#
.SYNOPSIS
    PowerShell Hybrid Worker - Main entry point.
.DESCRIPTION
    On-premises PowerShell worker that processes migration automation jobs via
    Azure Service Bus using a PS 5.1 PSSession pool for on-prem and cloud
    service functions (AD, Exchange Server, SPO, Teams).

    Can be invoked directly or dot-sourced from Start-HybridWorker.ps1 (the
    scheduled task launcher). When launched via the launcher, logging and Az
    auth are already initialized and those phases are skipped.
.NOTES
    Configuration is loaded from a JSON file. See config.ps1 for details.
#>

#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$script:WorkerRunning = $true

# Determine base path
$basePath = Split-Path -Parent $PSScriptRoot
if (-not $basePath) { $basePath = $PSScriptRoot }
$srcPath = Join-Path $basePath 'src'

# Dot-source all worker components
. (Join-Path $srcPath 'config.ps1')
. (Join-Path $srcPath 'logging.ps1')
. (Join-Path $srcPath 'auth.ps1')
. (Join-Path $srcPath 'service-bus.ps1')
. (Join-Path $srcPath 'ad-forest-manager.ps1')
. (Join-Path $srcPath 'session-pool.ps1')
. (Join-Path $srcPath 'service-connections.ps1')
. (Join-Path $srcPath 'update-manager.ps1')
. (Join-Path $srcPath 'job-dispatcher.ps1')
. (Join-Path $srcPath 'health-check.ps1')

# --- Startup Banner ---
Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  PowerShell Hybrid Worker' -ForegroundColor Cyan
Write-Host '  Migration Automation Toolkit' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# --- Phase 1: Apply Pending Update ---
Write-Host '[BOOT] Phase 1: Checking for pending updates...'
$installPath = $env:HYBRID_WORKER_INSTALL_PATH ?? 'C:\ProgramData\MaToolkit\HybridWorker'
$updateApplied = Apply-PendingUpdate -InstallPath $installPath
if ($updateApplied) {
    Write-Host '[BOOT] Update applied. Worker will use the new version.'
}

# --- Phase 2: Load Configuration ---
Write-Host '[BOOT] Phase 2: Loading configuration...'
try {
    $config = Get-WorkerConfiguration
    Write-Host "[BOOT] Worker ID: $($config.WorkerId)"
    Write-Host "[BOOT] Max PS 5.1 Sessions: $($config.MaxPs51Sessions)"
    Write-Host "[BOOT] Service Bus: $($config.ServiceBusNamespace)"
    Write-Host "[BOOT] Idle Timeout: $(if ($config.IdleTimeoutSeconds -gt 0) { "$($config.IdleTimeoutSeconds)s" } else { 'disabled (persistent service)' })"
}
catch {
    Write-Host "[FATAL] Configuration error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# --- Phase 3: Initialize Logging ---
if ($script:LoggingInitialized) {
    Write-WorkerLog -Message 'Phase 3: Logging already initialized (launcher mode). Skipping.'
}
else {
    Write-Host '[BOOT] Phase 3: Initializing logging...'
    try {
        Initialize-WorkerLogging -Config $config
    }
    catch {
        Write-Host "[FATAL] Logging initialization error: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}
Write-WorkerLog -Message "Worker '$($config.WorkerId)' starting up..."
Write-WorkerEvent -EventName 'WorkerStarting' -Properties @{
    MaxPs51Sessions     = $config.MaxPs51Sessions
    ServiceBusNamespace = $config.ServiceBusNamespace
}

# --- Phase 4: Authenticate to Azure (SP + cert) ---
if (Get-AzContext -ErrorAction SilentlyContinue) {
    Write-WorkerLog -Message 'Phase 4: Azure context already established (launcher mode). Skipping.'
}
else {
    Write-WorkerLog -Message 'Phase 4: Azure authentication (service principal + certificate)...'
    try {
        Connect-HybridWorkerAzure -Config $config
    }
    catch {
        Write-WorkerLog -Message "Azure authentication failed: $($_.Exception.Message)" -Severity Critical
        Flush-WorkerTelemetry
        exit 1
    }
}

# --- Phase 5: Retrieve Credentials ---
Write-WorkerLog -Message 'Phase 5: Retrieving credentials...'
$onPremCredentials = @{}
$forestCredentials = @{}

# Retrieve forest credentials (if AD enabled)
if ($config.ServiceConnections.activeDirectory.enabled -eq $true) {
    try {
        $forests = Initialize-ForestManager -Forests @($config.ServiceConnections.activeDirectory.forests)
        $forestCredentials = Get-ForestCredentials -KeyVaultName $config.KeyVaultName -Forests $forests
    }
    catch {
        Write-WorkerLog -Message "Forest credential retrieval failed: $($_.Exception.Message)" -Severity Critical
        Flush-WorkerTelemetry
        exit 1
    }
}

# Retrieve individual service credentials
$credentialServices = @('exchangeServer', 'sharepointOnline', 'teams')
foreach ($svc in $credentialServices) {
    if ($config.ServiceConnections.$svc.enabled -eq $true) {
        $credSecret = $config.ServiceConnections.$svc.credentialSecret
        if ($credSecret) {
            Write-WorkerLog -Message "Retrieving credential for '$svc'..."
            try {
                $onPremCredentials[$svc] = Get-OnPremCredential -KeyVaultName $config.KeyVaultName -SecretName $credSecret
            }
            catch {
                Write-WorkerLog -Message "Failed to retrieve credential for '$svc': $($_.Exception.Message)" -Severity Critical
                Flush-WorkerTelemetry
                exit 1
            }
        }
    }
}

$anyServiceEnabled = ($config.ServiceConnections.activeDirectory.enabled -eq $true) -or
                     ($credentialServices | Where-Object { $config.ServiceConnections.$_.enabled -eq $true }).Count -gt 0
if (-not $anyServiceEnabled) {
    Write-WorkerLog -Message 'Phase 5: No services enabled -- no credentials to retrieve.'
}

# --- Phase 6: Initialize Service Bus ---
Write-WorkerLog -Message 'Phase 6: Initializing Service Bus...'
try {
    Initialize-ServiceBusAssemblies -DotNetLibPath $config.DotNetLibPath
    $sbClient = New-ServiceBusClient -Namespace $config.ServiceBusNamespace -Config $config
    $sbReceiver = New-ServiceBusReceiver -Client $sbClient -TopicName $config.JobsTopicName -WorkerId $config.WorkerId
    $sbSender = New-ServiceBusSender -Client $sbClient -TopicName $config.ResultsTopicName
}
catch {
    Write-WorkerLog -Message "Service Bus initialization failed: $($_.Exception.Message)" -Severity Critical
    Write-WorkerException -Exception $_.Exception
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 7: Initialize Service Connections ---
Write-WorkerLog -Message 'Phase 7: Initializing service connections...'
try {
    $serviceRegistry = Initialize-ServiceConnections -Config $config
}
catch {
    Write-WorkerLog -Message "Service connections initialization failed: $($_.Exception.Message)" -Severity Critical
    Write-WorkerException -Exception $_.Exception
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 8: Initialize SessionPool ---
Write-WorkerLog -Message 'Phase 8: Initializing SessionPool...'

# Build forest config hashtable for injection into sessions
$forestConfigsForSessions = @{}
if ($config.ServiceConnections.activeDirectory.enabled -eq $true) {
    foreach ($forest in @($config.ServiceConnections.activeDirectory.forests)) {
        $forestConfigsForSessions[$forest.name] = @{
            Server     = $forest.server
            Credential = $forestCredentials[$forest.name]
        }
    }
}

$sessionPool = $null
if ($serviceRegistry.EnabledServices.Count -gt 0) {
    try {
        $sessionPool = Initialize-SessionPool -Config $config -OnPremCredentials $onPremCredentials `
            -ForestConfigs $forestConfigsForSessions -EnabledModulePaths $serviceRegistry.EnabledModulePaths
    }
    catch {
        Write-WorkerLog -Message "SessionPool initialization failed: $($_.Exception.Message)" -Severity Critical
        Write-WorkerException -Exception $_.Exception
        try { $sbReceiver.DisposeAsync().GetAwaiter().GetResult() } catch { }
        try { $sbSender.DisposeAsync().GetAwaiter().GetResult() } catch { }
        try { $sbClient.DisposeAsync().GetAwaiter().GetResult() } catch { }
        Flush-WorkerTelemetry
        exit 1
    }
}
else {
    Write-WorkerLog -Message 'Phase 8: Skipped (no services enabled).'
}

# --- Phase 9: Start Health Check Server ---
$healthCheckJob = $null
if ($config.HealthCheckEnabled) {
    Write-WorkerLog -Message 'Phase 9: Starting health check server...'
    $healthCheckJob = Start-Job -ScriptBlock {
        param($srcPath, $Port, $WorkerRunning, $SessionPool, $ServiceBusReceiver, $Config)
        . (Join-Path $srcPath 'health-check.ps1')
        Start-HealthCheckServer -Port $Port -WorkerRunning $WorkerRunning -SessionPool $SessionPool -ServiceBusReceiver $ServiceBusReceiver -Config $Config
    } -ArgumentList $srcPath, $config.HealthCheckPort, ([ref]$script:WorkerRunning), $sessionPool, $sbReceiver, $config
    Write-WorkerLog -Message "Health check server started on port $($config.HealthCheckPort)"
}
else {
    Write-WorkerLog -Message 'Phase 9: Health check disabled. Skipping.'
}

# --- Phase 10: Register Shutdown Handler ---
Write-WorkerLog -Message 'Phase 10: Registering shutdown handler...'

# Register shutdown handler for service host stop signal.
try {
    [Console]::CancelKeyPress.Add({
        param($sender, $e)
        $e.Cancel = $true
        $script:WorkerRunning = $false
    }) | Out-Null
}
catch {
    Write-WorkerLog -Message 'Could not register console cancel handler.' -Severity Verbose
}

# Also register PowerShell.Exiting
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
    $script:WorkerRunning = $false
}

# --- Phase 11: Start Job Dispatcher ---
Write-WorkerLog -Message 'Phase 11: Starting job dispatcher...'
Write-WorkerEvent -EventName 'WorkerReady' -Properties @{
    WorkerId           = $config.WorkerId
    MaxPs51Sessions    = $config.MaxPs51Sessions
    EnabledServices    = ($serviceRegistry.EnabledServices -join ', ')
    AllowedFunctions   = $serviceRegistry.AllowedFunctions.Count
    CatalogFunctions   = $serviceRegistry.FunctionCatalog.Count
}

Write-Host ''
Write-Host "Worker '$($config.WorkerId)' is READY and listening for jobs." -ForegroundColor Green
Write-Host ''

if ($null -eq $sessionPool) {
    Write-WorkerLog -Message 'No services enabled. Worker will only respond to health checks until shutdown.' -Severity Warning
    while ($script:WorkerRunning) { Start-Sleep -Seconds 5 }
}
else {
    try {
        Start-JobDispatcher -Config $config -Receiver $sbReceiver -Sender $sbSender -Client $sbClient `
            -JobsTopicName $config.JobsTopicName -ServiceRegistry $serviceRegistry `
            -SessionPool $sessionPool `
            -Running ([ref]$script:WorkerRunning)
    }
    catch {
        Write-WorkerLog -Message "Job dispatcher fatal error: $($_.Exception.Message)" -Severity Critical
        Write-WorkerException -Exception $_.Exception
    }
}

# --- Shutdown ---
Write-WorkerLog -Message 'Worker shutting down...'

# Stop health check server
try {
    if ($null -ne $healthCheckJob) {
        Stop-Job -Job $healthCheckJob -ErrorAction SilentlyContinue
        Remove-Job -Job $healthCheckJob -Force -ErrorAction SilentlyContinue
        Write-WorkerLog -Message 'Health check server stopped.'
    }
}
catch {
    Write-WorkerLog -Message "Error stopping health check server: $($_.Exception.Message)" -Severity Warning
}

# Close session pool
if ($null -ne $sessionPool) {
    try {
        Close-SessionPool -Pool $sessionPool
    }
    catch {
        Write-WorkerLog -Message "Error closing session pool: $($_.Exception.Message)" -Severity Warning
    }
}

try {
    $sbReceiver.DisposeAsync().GetAwaiter().GetResult()
    $sbSender.DisposeAsync().GetAwaiter().GetResult()
    $sbClient.DisposeAsync().GetAwaiter().GetResult()
    Write-WorkerLog -Message 'Service Bus resources disposed.'
}
catch {
    Write-WorkerLog -Message "Error disposing Service Bus: $($_.Exception.Message)" -Severity Warning
}

Write-WorkerEvent -EventName 'WorkerStopped'
Flush-WorkerTelemetry

Write-Host ''
Write-Host "Worker '$($config.WorkerId)' has stopped." -ForegroundColor Yellow
Write-Host ''

exit 0
