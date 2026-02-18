<#
.SYNOPSIS
    PowerShell Hybrid Worker - Main entry point.
.DESCRIPTION
    On-premises PowerShell worker that runs as a native Windows Service. Processes
    migration automation jobs via Azure Service Bus using a dual-engine architecture:
    PS 7.x RunspacePool for cloud functions and PS 5.1 PSSession pool for on-prem functions.
.NOTES
    Configuration is loaded from a JSON file. See config.ps1 for details.
    The .NET service host sets HYBRID_WORKER_CONFIG_PATH and HYBRID_WORKER_INSTALL_PATH.
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
. (Join-Path $srcPath 'runspace-manager.ps1')
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
    Write-Host "[BOOT] Max Parallelism: $($config.MaxParallelism)"
    Write-Host "[BOOT] Max PS 5.1 Sessions: $($config.MaxPs51Sessions)"
    Write-Host "[BOOT] Service Bus: $($config.ServiceBusNamespace)"
    Write-Host "[BOOT] Idle Timeout: $(if ($config.IdleTimeoutSeconds -gt 0) { "$($config.IdleTimeoutSeconds)s" } else { 'disabled (persistent service)' })"
}
catch {
    Write-Host "[FATAL] Configuration error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# --- Phase 3: Initialize Logging ---
Write-Host '[BOOT] Phase 3: Initializing logging...'
try {
    Initialize-WorkerLogging -Config $config
    Write-WorkerLog -Message "Worker '$($config.WorkerId)' starting up..."
    Write-WorkerEvent -EventName 'WorkerStarting' -Properties @{
        MaxParallelism      = $config.MaxParallelism
        MaxPs51Sessions     = $config.MaxPs51Sessions
        ServiceBusNamespace = $config.ServiceBusNamespace
    }
}
catch {
    Write-Host "[FATAL] Logging initialization error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# --- Phase 4: Authenticate to Azure (SP + cert) ---
Write-WorkerLog -Message 'Phase 4: Azure authentication (service principal + certificate)...'
try {
    Connect-HybridWorkerAzure -Config $config
}
catch {
    Write-WorkerLog -Message "Azure authentication failed: $($_.Exception.Message)" -Severity Critical
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 5: Retrieve Target Tenant Certificate (conditional) ---
$certificate = $null
$cloudServicesEnabled = ($config.ServiceConnections.entra.enabled -eq $true) -or
                        ($config.ServiceConnections.exchangeOnline.enabled -eq $true)
if ($cloudServicesEnabled) {
    Write-WorkerLog -Message 'Phase 5: Retrieving target tenant certificate from Key Vault...'
    try {
        $certificate = Get-WorkerCertificate -KeyVaultName $config.KeyVaultName -CertificateName $config.TargetCertificateName
    }
    catch {
        Write-WorkerLog -Message "Certificate retrieval failed: $($_.Exception.Message)" -Severity Critical
        Flush-WorkerTelemetry
        exit 1
    }
}
else {
    Write-WorkerLog -Message 'Phase 5: Skipped (no cloud services enabled).'
}

# --- Phase 6: Retrieve On-Prem Credentials (conditional) ---
$onPremCredentials = @{}
$onPremServices = @('activeDirectory', 'exchangeServer', 'sharepointOnline', 'teams')
$onPremServicesEnabled = $false
foreach ($svc in $onPremServices) {
    if ($config.ServiceConnections.$svc.enabled -eq $true) {
        $onPremServicesEnabled = $true
        $credSecret = $config.ServiceConnections.$svc.credentialSecret
        if ($credSecret) {
            Write-WorkerLog -Message "Phase 6: Retrieving credential for '$svc'..."
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
if (-not $onPremServicesEnabled) {
    Write-WorkerLog -Message 'Phase 6: Skipped (no on-prem services enabled).'
}

# --- Phase 7: Initialize Service Bus ---
Write-WorkerLog -Message 'Phase 7: Initializing Service Bus...'
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

# --- Phase 8: Initialize Service Connections ---
Write-WorkerLog -Message 'Phase 8: Initializing service connections...'
try {
    $serviceRegistry = Initialize-ServiceConnections -Config $config
}
catch {
    Write-WorkerLog -Message "Service connections initialization failed: $($_.Exception.Message)" -Severity Critical
    Write-WorkerException -Exception $_.Exception
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 9: Initialize Execution Engines ---
$runspacePool = $null
$sessionPool = $null

# Phase 9a: RunspacePool (if cloud services enabled)
if ($serviceRegistry.CloudServicesEnabled) {
    Write-WorkerLog -Message 'Phase 9a: Initializing RunspacePool for PS 7.x cloud functions...'
    try {
        $runspacePool = Initialize-RunspacePool -Config $config -Certificate $certificate
    }
    catch {
        Write-WorkerLog -Message "RunspacePool initialization failed: $($_.Exception.Message)" -Severity Critical
        Write-WorkerException -Exception $_.Exception
        try { $sbReceiver.DisposeAsync().GetAwaiter().GetResult() } catch { }
        try { $sbSender.DisposeAsync().GetAwaiter().GetResult() } catch { }
        try { $sbClient.DisposeAsync().GetAwaiter().GetResult() } catch { }
        Flush-WorkerTelemetry
        exit 1
    }
}
else {
    Write-WorkerLog -Message 'Phase 9a: Skipped (no cloud services enabled).'
}

# Phase 9b: SessionPool (if on-prem services enabled)
if ($serviceRegistry.OnPremServicesEnabled) {
    Write-WorkerLog -Message 'Phase 9b: Initializing SessionPool for PS 5.1 on-prem functions...'
    try {
        $sessionPool = Initialize-SessionPool -Config $config -OnPremCredentials $onPremCredentials
    }
    catch {
        Write-WorkerLog -Message "SessionPool initialization failed: $($_.Exception.Message)" -Severity Critical
        Write-WorkerException -Exception $_.Exception
        if ($runspacePool) { try { Close-RunspacePool -Pool $runspacePool } catch { } }
        try { $sbReceiver.DisposeAsync().GetAwaiter().GetResult() } catch { }
        try { $sbSender.DisposeAsync().GetAwaiter().GetResult() } catch { }
        try { $sbClient.DisposeAsync().GetAwaiter().GetResult() } catch { }
        Flush-WorkerTelemetry
        exit 1
    }
}
else {
    Write-WorkerLog -Message 'Phase 9b: Skipped (no on-prem services enabled).'
}

# --- Phase 10: Start Health Check Server ---
Write-WorkerLog -Message 'Phase 10: Starting health check server...'
$healthCheckJob = Start-Job -ScriptBlock {
    param($srcPath, $Port, $WorkerRunning, $RunspacePool, $SessionPool, $ServiceBusReceiver, $Config)
    . (Join-Path $srcPath 'health-check.ps1')
    Start-HealthCheckServer -Port $Port -WorkerRunning $WorkerRunning -RunspacePool $RunspacePool -SessionPool $SessionPool -ServiceBusReceiver $ServiceBusReceiver -Config $Config
} -ArgumentList $srcPath, $config.HealthCheckPort, ([ref]$script:WorkerRunning), $runspacePool, $sessionPool, $sbReceiver, $config
Write-WorkerLog -Message "Health check server started on port $($config.HealthCheckPort)"

# --- Phase 11: Register Shutdown Handler ---
Write-WorkerLog -Message 'Phase 11: Registering shutdown handler...'

# Register shutdown handler for service host stop signal.
# The .NET WorkerProcessService sends a process termination signal on service stop.
# Console.CancelKeyPress catches this and sets the running flag to false, allowing
# the dispatcher to drain active jobs within the configured grace period.
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

# --- Phase 12: Start Job Dispatcher ---
Write-WorkerLog -Message 'Phase 12: Starting job dispatcher...'
Write-WorkerEvent -EventName 'WorkerReady' -Properties @{
    WorkerId           = $config.WorkerId
    MaxParallelism     = $config.MaxParallelism
    MaxPs51Sessions    = $config.MaxPs51Sessions
    CloudServices      = $serviceRegistry.CloudServicesEnabled
    OnPremServices     = $serviceRegistry.OnPremServicesEnabled
    EnabledServices    = ($serviceRegistry.EnabledServices -join ', ')
}

Write-Host ''
Write-Host "Worker '$($config.WorkerId)' is READY and listening for jobs." -ForegroundColor Green
Write-Host ''

try {
    Start-JobDispatcher -Config $config -Receiver $sbReceiver -Sender $sbSender -Client $sbClient `
        -JobsTopicName $config.JobsTopicName -ServiceRegistry $serviceRegistry `
        -RunspacePool $runspacePool -SessionPool $sessionPool `
        -Running ([ref]$script:WorkerRunning)
}
catch {
    Write-WorkerLog -Message "Job dispatcher fatal error: $($_.Exception.Message)" -Severity Critical
    Write-WorkerException -Exception $_.Exception
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

# Close execution engines
if ($null -ne $sessionPool) {
    try {
        Close-SessionPool -Pool $sessionPool
    }
    catch {
        Write-WorkerLog -Message "Error closing session pool: $($_.Exception.Message)" -Severity Warning
    }
}

if ($null -ne $runspacePool) {
    try {
        Close-RunspacePool -Pool $runspacePool
    }
    catch {
        Write-WorkerLog -Message "Error closing runspace pool: $($_.Exception.Message)" -Severity Warning
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
