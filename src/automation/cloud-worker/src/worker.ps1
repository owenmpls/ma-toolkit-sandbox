<#
.SYNOPSIS
    PowerShell Cloud Worker - Main entry point.
.DESCRIPTION
    Containerized PowerShell worker for Azure Container Apps that processes
    migration automation jobs via Azure Service Bus. Executes functions from
    standard and custom modules against Microsoft Graph and Exchange Online.
.NOTES
    Configuration is loaded from environment variables. See config.ps1 for details.
#>

#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$script:WorkerRunning = $true

# Determine base path
$basePath = Split-Path -Parent $PSScriptRoot
if (-not $basePath) { $basePath = '/app' }
$srcPath = Join-Path $basePath 'src'

# Dot-source all worker components
. (Join-Path $srcPath 'config.ps1')
. (Join-Path $srcPath 'logging.ps1')
. (Join-Path $srcPath 'auth.ps1')
. (Join-Path $srcPath 'servicebus.ps1')
. (Join-Path $srcPath 'runspace-manager.ps1')
. (Join-Path $srcPath 'job-dispatcher.ps1')
. (Join-Path $srcPath 'health-check.ps1')

# --- Startup Banner ---
Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  PowerShell Cloud Worker' -ForegroundColor Cyan
Write-Host '  Migration Automation Toolkit' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# --- Phase 1: Load Configuration ---
Write-Host '[BOOT] Loading configuration...'
try {
    $config = Get-WorkerConfiguration
    Write-Host "[BOOT] Worker ID: $($config.WorkerId)"
    Write-Host "[BOOT] Max Parallelism: $($config.MaxParallelism)"
    Write-Host "[BOOT] Target Tenant: $($config.TargetTenantId)"
    Write-Host "[BOOT] Service Bus: $($config.ServiceBusNamespace)"
    Write-Host "[BOOT] Idle Timeout: $(if ($config.IdleTimeoutSeconds -gt 0) { "$($config.IdleTimeoutSeconds)s" } else { 'disabled' })"
}
catch {
    Write-Host "[FATAL] Configuration error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# --- Phase 2: Initialize Logging ---
Write-Host '[BOOT] Initializing logging...'
try {
    Initialize-WorkerLogging -Config $config
    Write-WorkerLog -Message "Worker '$($config.WorkerId)' starting up..."
    Write-WorkerEvent -EventName 'WorkerStarting' -Properties @{
        MaxParallelism     = $config.MaxParallelism
        TargetTenantId     = $config.TargetTenantId
        ServiceBusNamespace = $config.ServiceBusNamespace
    }
}
catch {
    Write-Host "[FATAL] Logging initialization error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# --- Phase 3: Authenticate to Azure (for Key Vault access) ---
Write-WorkerLog -Message 'Phase 3: Azure authentication for Key Vault access...'
try {
    Connect-WorkerAzure
}
catch {
    Write-WorkerLog -Message "Azure authentication failed: $($_.Exception.Message)" -Severity Critical
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 4: Retrieve Certificate ---
Write-WorkerLog -Message 'Phase 4: Retrieving application certificate from Key Vault...'
try {
    $certificate = Get-WorkerCertificate -KeyVaultName $config.KeyVaultName -CertificateName $config.CertificateName
}
catch {
    Write-WorkerLog -Message "Certificate retrieval failed: $($_.Exception.Message)" -Severity Critical
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 5: Initialize Service Bus ---
Write-WorkerLog -Message 'Phase 5: Initializing Service Bus...'
try {
    Initialize-ServiceBusAssemblies -DotNetLibPath $config.DotNetLibPath
    $sbClient = New-ServiceBusClient -Namespace $config.ServiceBusNamespace
    $sbReceiver = New-ServiceBusReceiver -Client $sbClient -TopicName $config.JobsTopicName -WorkerId $config.WorkerId
    $sbSender = New-ServiceBusSender -Client $sbClient -TopicName $config.ResultsTopicName
}
catch {
    Write-WorkerLog -Message "Service Bus initialization failed: $($_.Exception.Message)" -Severity Critical
    Write-WorkerException -Exception $_.Exception
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 6: Initialize Runspace Pool ---
Write-WorkerLog -Message 'Phase 6: Initializing runspace pool with authenticated sessions...'
try {
    $runspacePool = Initialize-RunspacePool -Config $config -Certificate $certificate
}
catch {
    Write-WorkerLog -Message "Runspace pool initialization failed: $($_.Exception.Message)" -Severity Critical
    Write-WorkerException -Exception $_.Exception
    # Dispose Service Bus resources from Phase 5
    try { $sbReceiver.DisposeAsync().GetAwaiter().GetResult() } catch { }
    try { $sbSender.DisposeAsync().GetAwaiter().GetResult() } catch { }
    try { $sbClient.DisposeAsync().GetAwaiter().GetResult() } catch { }
    Flush-WorkerTelemetry
    exit 1
}

# --- Phase 7: Start Health Check Server ---
Write-WorkerLog -Message 'Phase 7: Starting health check server...'
$healthCheckPort = $env:HEALTH_CHECK_PORT ?? 8080
$healthCheckJob = Start-Job -ScriptBlock {
    param($srcPath, $Port, $WorkerRunning, $RunspacePool, $ServiceBusReceiver, $Config)
    . (Join-Path $srcPath 'health-check.ps1')
    Start-HealthCheckServer -Port $Port -WorkerRunning $WorkerRunning -RunspacePool $RunspacePool -ServiceBusReceiver $ServiceBusReceiver -Config $Config
} -ArgumentList $srcPath, $healthCheckPort, ([ref]$script:WorkerRunning), $runspacePool, $sbReceiver, $config
Write-WorkerLog -Message "Health check server started on port $healthCheckPort"

# --- Phase 8: Register Shutdown Handler ---
Write-WorkerLog -Message 'Phase 8: Registering shutdown handler...'

$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
    $script:WorkerRunning = $false
}

# Handle SIGTERM / SIGINT for graceful container shutdown
$shutdownHandler = {
    Write-WorkerLog -Message 'Shutdown signal received. Initiating graceful shutdown...'
    $script:WorkerRunning = $false
}

try {
    [Console]::CancelKeyPress.Add({
        param($sender, $e)
        $e.Cancel = $true
        $script:WorkerRunning = $false
    }) | Out-Null
}
catch {
    Write-WorkerLog -Message 'Could not register console cancel handler (non-interactive mode).' -Severity Verbose
}

# --- Phase 9: Start Job Dispatcher ---
Write-WorkerLog -Message 'Phase 9: Starting job dispatcher...'
Write-WorkerEvent -EventName 'WorkerReady' -Properties @{
    WorkerId       = $config.WorkerId
    MaxParallelism = $config.MaxParallelism
}

Write-Host ''
Write-Host "Worker '$($config.WorkerId)' is READY and listening for jobs." -ForegroundColor Green
Write-Host ''

try {
    Start-JobDispatcher -Config $config -Receiver $sbReceiver -Sender $sbSender -Pool $runspacePool -Running ([ref]$script:WorkerRunning)
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

try {
    Close-RunspacePool -Pool $runspacePool
}
catch {
    Write-WorkerLog -Message "Error closing runspace pool: $($_.Exception.Message)" -Severity Warning
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
