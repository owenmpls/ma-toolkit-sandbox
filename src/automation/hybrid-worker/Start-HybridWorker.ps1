<#
.SYNOPSIS
    Launcher for the MA Toolkit Hybrid Worker (Scheduled Task mode).
.DESCRIPTION
    Invoked by a Scheduled Task every N minutes. Performs a lightweight "tick":
    applies pending updates, initializes logging, authenticates to Azure,
    checks for new updates, peeks Service Bus for messages, and logs a
    LauncherTick event. If messages are found (or peek fails -- fail-open),
    dot-sources worker.ps1 for a full work cycle.

    Overlap prevention is handled by Task Scheduler's MultipleInstances = IgnoreNew.
    If the worker is still running from a previous tick, the new instance is skipped.
.NOTES
    This script reuses functions from src/ (config.ps1, logging.ps1, auth.ps1,
    update-manager.ps1, service-bus.ps1). The worker.ps1 detects that logging
    and Az auth are already initialized and skips re-doing those phases.
#>

#Requires -Version 7.4

$ErrorActionPreference = 'Stop'

# Determine base path (hybrid-worker/ root)
$basePath = $PSScriptRoot
$srcPath = Join-Path $basePath 'src'

# Dot-source required components
. (Join-Path $srcPath 'config.ps1')
. (Join-Path $srcPath 'logging.ps1')
. (Join-Path $srcPath 'auth.ps1')
. (Join-Path $srcPath 'update-manager.ps1')
. (Join-Path $srcPath 'service-bus.ps1')

# --- Step 1: Apply Pending Update ---
$installPath = $env:HYBRID_WORKER_INSTALL_PATH ?? 'C:\ProgramData\MaToolkit\HybridWorker'
$updateApplied = Apply-PendingUpdate -InstallPath $installPath

# --- Step 2: Load Configuration ---
try {
    $config = Get-WorkerConfiguration
}
catch {
    Write-Host "[FATAL] Configuration error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# --- Step 3: Read version ---
$versionFile = Join-Path $installPath 'current\version.txt'
$workerVersion = if (Test-Path $versionFile) {
    (Get-Content $versionFile -Raw).Trim()
} else {
    # Fall back to version.txt next to this script (dev/source layout)
    $localVersion = Join-Path $basePath 'version.txt'
    if (Test-Path $localVersion) { (Get-Content $localVersion -Raw).Trim() } else { '0.0.0' }
}

# --- Step 4: Initialize Logging ---
try {
    Initialize-WorkerLogging -Config $config
}
catch {
    Write-Host "[FATAL] Logging initialization error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# --- Step 5: Azure Authentication ---
try {
    Connect-HybridWorkerAzure -Config $config
}
catch {
    Write-WorkerLog -Message "Azure authentication failed: $($_.Exception.Message)" -Severity Critical
    Flush-WorkerTelemetry
    exit 1
}

# --- Step 6: Check for Updates ---
$updateAvailable = $false
$updateStaged = $false

if ($config.UpdateEnabled) {
    try {
        $updateInfo = Test-UpdateAvailable -Config $config
        if ($null -ne $updateInfo) {
            $updateAvailable = $true
            $updateStaged = Install-WorkerUpdate -Config $config -UpdateInfo $updateInfo
        }
    }
    catch {
        Write-WorkerLog -Message "Update check error: $($_.Exception.Message)" -Severity Warning
    }
}

# --- Step 7: Peek Service Bus for Messages ---
$messagesFound = 0
$peekFailed = $false

try {
    Initialize-ServiceBusAssemblies -DotNetLibPath $config.DotNetLibPath
    $sbCredential = Get-ServiceBusCredential -Config $config

    $clientOptions = [Azure.Messaging.ServiceBus.ServiceBusClientOptions]::new()
    $sbClient = [Azure.Messaging.ServiceBus.ServiceBusClient]::new(
        $config.ServiceBusNamespace, $sbCredential, $clientOptions)

    $subscriptionName = Get-SubscriptionName -WorkerId $config.WorkerId
    $receiverOptions = [Azure.Messaging.ServiceBus.ServiceBusReceiverOptions]::new()
    $receiverOptions.ReceiveMode = [Azure.Messaging.ServiceBus.ServiceBusReceiveMode]::PeekLock
    $sbReceiver = $sbClient.CreateReceiver($config.JobsTopicName, $subscriptionName, $receiverOptions)

    $peekTask = $sbReceiver.PeekMessagesAsync(1)
    $peeked = $peekTask.GetAwaiter().GetResult()
    $messagesFound = if ($null -ne $peeked) { $peeked.Count } else { 0 }
}
catch {
    Write-WorkerLog -Message "Service Bus peek failed: $($_.Exception.Message)" -Severity Warning
    $peekFailed = $true
}
finally {
    # Dispose SB resources from the peek
    try { if ($null -ne $sbReceiver) { $sbReceiver.DisposeAsync().GetAwaiter().GetResult() } } catch { }
    try { if ($null -ne $sbClient) { $sbClient.DisposeAsync().GetAwaiter().GetResult() } } catch { }
}

# --- Step 8: Determine action ---
$action = if ($messagesFound -gt 0 -or $peekFailed) { 'StartingWorker' } else { 'NoWork' }

# --- Step 9: Log LauncherTick Event ---
Write-WorkerEvent -EventName 'LauncherTick' -Properties @{
    Version         = $workerVersion
    MessagesFound   = $messagesFound
    UpdateAvailable = $updateAvailable
    UpdateStaged    = $updateStaged
    UpdateApplied   = $updateApplied
    PeekFailed      = $peekFailed
    Action          = $action
}

Flush-WorkerTelemetry

# --- Step 10: Exit or invoke worker ---
if ($action -eq 'NoWork') {
    Write-WorkerLog -Message "No messages found. Exiting."
    exit 0
}

Write-WorkerLog -Message "Messages found ($messagesFound) or peek failed. Starting full worker..."

# Dot-source the full worker — it will detect that logging and Az auth
# are already initialized and skip those phases.
. (Join-Path $srcPath 'worker.ps1')
