<#
.SYNOPSIS
    Bulk-exports Exchange Online mailbox data to JSONL for Databricks ingestion.
.DESCRIPTION
    Exports mailbox metadata, mailbox statistics, and optionally folder statistics
    for an entire tenant. Designed for hundreds of thousands of mailboxes.

    Phase 1 streams Get-EXOMailbox to a single JSONL file (serial).
    Phase 2 uses a RunspacePool to call Get-EXOMailboxStatistics (and optionally
    Get-EXOMailboxFolderStatistics) in parallel, writing per-runspace chunk files.

    Output structure:
      {OutputPath}/YYYY-MM-DD_HHmmss/
        mailboxes.jsonl
        mailbox-statistics/chunk-001.jsonl ...
        mailbox-folder-statistics/chunk-001.jsonl ...  (if -IncludeFolderStatistics)
        progress.json
        export-summary.json
.EXAMPLE
    .\Export-EXOMailboxData.ps1 -AppId "00000000-..." -Organization "contoso.onmicrosoft.com" `
        -CertificatePath .\cert.pfx -CertificatePassword (Read-Host -AsSecureString) `
        -MaxParallelism 10
.EXAMPLE
    .\Export-EXOMailboxData.ps1 -AppId "00000000-..." -Organization "contoso.onmicrosoft.com" `
        -CertificateThumbprint "ABC123..." -IncludeFolderStatistics -Resume
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AppId,

    [Parameter(Mandatory)]
    [string]$Organization,

    [Parameter(Mandatory, ParameterSetName = 'PFXFile')]
    [string]$CertificatePath,

    [Parameter(Mandatory, ParameterSetName = 'PFXFile')]
    [SecureString]$CertificatePassword,

    [Parameter(Mandatory, ParameterSetName = 'Thumbprint')]
    [string]$CertificateThumbprint,

    [string]$OutputPath = './exo-export',

    [switch]$IncludeFolderStatistics,

    [ValidateRange(1, 20)]
    [int]$MaxParallelism = 5,

    [switch]$Resume,

    [int]$MaxRetries = 5,
    [int]$BaseDelaySeconds = 2,
    [int]$MaxDelaySeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ctrl+C handling — sets flag so loops exit gracefully
$script:Running = $true
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action { $script:Running = $false }
[Console]::TreatControlCAsInput = $false
$null = [Console]::CancelKeyPress.GetType()  # ensure type is loaded
$cancelHandler = [ConsoleCancelEventHandler]{
    param($sender, $eventArgs)
    $eventArgs.Cancel = $true
    $script:Running = $false
    Write-Host "`nCancellation requested — finishing in-flight work..."
}
[Console]::add_CancelKeyPress($cancelHandler)

#region Functions

function Write-ExportLog {
    <#
    .SYNOPSIS
        Writes a timestamped log message to the console.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [ValidateSet('Info', 'Warning', 'Error')]
        [string]$Severity = 'Info'
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $prefix = switch ($Severity) {
        'Info'    { 'INF' }
        'Warning' { 'WRN' }
        'Error'   { 'ERR' }
    }
    $line = "[$timestamp] [$prefix] $Message"
    switch ($Severity) {
        'Error'   { Write-Host $line -ForegroundColor Red }
        'Warning' { Write-Host $line -ForegroundColor Yellow }
        default   { Write-Host $line }
    }
}

function Get-ExportCertificate {
    <#
    .SYNOPSIS
        Loads an X509Certificate2 from a PFX file or the certificate store.
    #>
    param(
        [string]$Path,
        [SecureString]$Password,
        [string]$Thumbprint
    )

    if ($Path) {
        $resolvedPath = Resolve-Path -Path $Path -ErrorAction Stop
        Write-ExportLog "Loading certificate from PFX: $resolvedPath"
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $resolvedPath.Path,
            $Password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        )
    }
    else {
        Write-ExportLog "Loading certificate from store: CurrentUser\My\$Thumbprint"
        $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
        if (-not $cert) {
            throw "Certificate with thumbprint '$Thumbprint' not found in CurrentUser\My store."
        }
    }

    if (-not $cert.HasPrivateKey) {
        throw "Certificate does not contain a private key. App-only auth requires a private key."
    }

    Write-ExportLog "Certificate loaded (Thumbprint: $($cert.Thumbprint), Subject: $($cert.Subject))"
    return $cert
}

function Export-Mailboxes {
    <#
    .SYNOPSIS
        Phase 1: Streams Get-EXOMailbox to a JSONL file, returns the list of mailbox IDs.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$FilePath
    )

    Write-ExportLog "Phase 1: Exporting mailboxes to $FilePath"
    $mailboxIds = [System.Collections.Generic.List[string]]::new()
    $count = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # StreamWriter for efficient line-by-line append
    $writer = [System.IO.StreamWriter]::new($FilePath, $false, [System.Text.Encoding]::UTF8)
    try {
        Get-EXOMailbox -PropertySets All -ResultSize Unlimited | ForEach-Object {
            if (-not $script:Running) { return }

            $json = $_ | ConvertTo-Json -Compress -Depth 5
            $writer.WriteLine($json)
            $mailboxIds.Add($_.ExternalDirectoryObjectId)
            $count++

            if ($count % 1000 -eq 0) {
                $writer.Flush()
                $elapsed = $sw.Elapsed.ToString('hh\:mm\:ss')
                Write-ExportLog "Phase 1: $count mailboxes exported ($elapsed elapsed)"
            }
        }
    }
    finally {
        $writer.Flush()
        $writer.Close()
        $writer.Dispose()
    }

    $sw.Stop()
    Write-ExportLog "Phase 1 complete: $count mailboxes exported in $($sw.Elapsed.ToString('hh\:mm\:ss'))"

    return @{
        MailboxIds = $mailboxIds
        Count      = $count
        Duration   = $sw.Elapsed
    }
}

function Initialize-ExportRunspacePool {
    <#
    .SYNOPSIS
        Creates a RunspacePool with ExchangeOnlineManagement imported and authenticates
        each runspace using certificate bytes.
    #>
    param(
        [Parameter(Mandatory)]
        [int]$PoolSize,

        [Parameter(Mandatory)]
        [string]$AppId,

        [Parameter(Mandatory)]
        [string]$Organization,

        [Parameter(Mandatory)]
        [byte[]]$CertificateBytes
    )

    Write-ExportLog "Initializing runspace pool (size=$PoolSize)..."

    $initialState = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    $initialState.ImportPSModule('ExchangeOnlineManagement')

    $pool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(
        1,
        $PoolSize,
        $initialState,
        (Get-Host)
    )
    $pool.Open()

    # Authenticate each runspace in parallel
    $authScript = {
        param($AppId, $Organization, $CertificateBytes, $RunspaceIndex)

        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $CertificateBytes,
            [string]::Empty,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        )

        $exoParams = @{
            Certificate  = $cert
            AppId        = $AppId
            Organization = $Organization
            ShowBanner   = $false
            ErrorAction  = 'Stop'
        }
        Connect-ExchangeOnline @exoParams

        # Store auth config for reactive reconnection on token expiry
        $global:ExportAuthConfig = @{
            AppId        = $AppId
            Organization = $Organization
        }
        $global:ExportCertBytes = $CertificateBytes

        return "Runspace ${RunspaceIndex}: Exchange Online connected."
    }

    $authHandles = @()
    for ($i = 0; $i -lt $PoolSize; $i++) {
        $ps = [PowerShell]::Create()
        $ps.RunspacePool = $pool

        $ps.AddScript($authScript).AddParameters(@{
            AppId            = $AppId
            Organization     = $Organization
            CertificateBytes = $CertificateBytes
            RunspaceIndex    = $i
        }) | Out-Null

        $handle = $ps.BeginInvoke()
        $authHandles += @{
            PowerShell = $ps
            Handle     = $handle
            Index      = $i
        }
    }

    $failedCount = 0
    foreach ($item in $authHandles) {
        try {
            $result = $item.PowerShell.EndInvoke($item.Handle)
            Write-ExportLog "$result"

            if ($item.PowerShell.HadErrors) {
                foreach ($err in $item.PowerShell.Streams.Error) {
                    Write-ExportLog "Runspace $($item.Index) auth warning: $($err.Exception.Message)" -Severity Warning
                }
            }
        }
        catch {
            $failedCount++
            Write-ExportLog "Runspace $($item.Index) auth failed: $($_.Exception.Message)" -Severity Error
        }
        finally {
            $item.PowerShell.Dispose()
        }
    }

    if ($failedCount -eq $PoolSize) {
        $pool.Close()
        $pool.Dispose()
        throw "All runspaces failed to authenticate. Cannot proceed."
    }

    if ($failedCount -gt 0) {
        Write-ExportLog "$failedCount runspace(s) failed auth. Running with reduced parallelism." -Severity Warning
    }

    Write-ExportLog "Runspace pool ready ($($PoolSize - $failedCount)/$PoolSize authenticated)."
    return $pool
}

function Export-MailboxStatistics {
    <#
    .SYNOPSIS
        Phase 2: Partitions mailbox IDs across runspaces and exports statistics in parallel.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Runspaces.RunspacePool]$Pool,

        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]]$MailboxIds,

        [Parameter(Mandatory)]
        [string]$StatsDir,

        [string]$FolderStatsDir,

        [Parameter(Mandatory)]
        [bool]$IncludeFolderStats,

        [Parameter(Mandatory)]
        [int]$MaxRetries,

        [Parameter(Mandatory)]
        [int]$BaseDelaySeconds,

        [Parameter(Mandatory)]
        [int]$MaxDelaySeconds,

        [Parameter(Mandatory)]
        [string]$ProgressPath
    )

    $totalCount = $MailboxIds.Count
    $poolSize = $Pool.GetMaxRunspaces()
    Write-ExportLog "Phase 2: Exporting statistics for $totalCount mailboxes across $poolSize runspaces"

    # Partition mailbox IDs into equal slices
    $slices = @()
    for ($i = 0; $i -lt $poolSize; $i++) {
        $slices += , [System.Collections.Generic.List[string]]::new()
    }
    for ($i = 0; $i -lt $totalCount; $i++) {
        $sliceIndex = $i % $poolSize
        $slices[$sliceIndex].Add($MailboxIds[$i])
    }

    # Scriptblock that runs inside each runspace
    $statsScript = {
        param(
            [string[]]$SliceIds,
            [string]$StatsFile,
            [string]$FolderStatsFile,
            [bool]$IncludeFolderStats,
            [int]$MaxRetries,
            [int]$BaseDelaySeconds,
            [int]$MaxDelaySeconds,
            [int]$ChunkIndex
        )

        # Inline retry helper
        function Invoke-WithRetry {
            param(
                [scriptblock]$Action,
                [int]$MaxRetries,
                [int]$BaseDelay,
                [int]$MaxDelay
            )

            $attempt = 0
            while ($true) {
                $attempt++
                try {
                    return (& $Action)
                }
                catch {
                    $ex = $_.Exception
                    $innermost = $ex
                    while ($innermost.InnerException) { $innermost = $innermost.InnerException }
                    $matchText = "$($ex.Message) $($innermost.Message)"

                    # Auth error — reconnect and retry
                    $isAuthError = $false
                    foreach ($pattern in @('401', 'Unauthorized', 'token.*expired', 'Access token has expired')) {
                        if ($matchText -match $pattern) { $isAuthError = $true; break }
                    }
                    if ($isAuthError -and $global:ExportAuthConfig -and $attempt -le $MaxRetries) {
                        try {
                            $cfg = $global:ExportAuthConfig
                            $certBytes = $global:ExportCertBytes
                            $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                                $certBytes,
                                [string]::Empty,
                                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
                            )
                            $exoParams = @{
                                Certificate  = $cert
                                AppId        = $cfg.AppId
                                Organization = $cfg.Organization
                                ShowBanner   = $false
                                ErrorAction  = 'Stop'
                            }
                            Connect-ExchangeOnline @exoParams
                        }
                        catch {
                            Write-Warning "Reconnect failed (attempt $attempt): $($_.Exception.Message)"
                        }
                        continue
                    }

                    # Throttle error — backoff and retry
                    $isThrottled = $false
                    $retryAfter = 0
                    foreach ($pattern in @(
                        'TooManyRequests', '429', 'throttled', 'Too many requests',
                        'Rate limit', 'Server Busy', 'ServerBusyException',
                        'MicroDelay', 'BackoffException', 'Too many concurrent',
                        'exceeded.*throttl', 'rate.*limit', 'please.*retry'
                    )) {
                        if ($matchText -match $pattern) { $isThrottled = $true; break }
                    }
                    if ($matchText -match 'Retry-After[:\s]+(\d+)') {
                        $retryAfter = [int]$Matches[1]
                    }
                    if ($isThrottled -and $attempt -le $MaxRetries) {
                        $delay = if ($retryAfter -gt 0) {
                            $retryAfter
                        }
                        else {
                            $exp = [math]::Min($BaseDelay * [math]::Pow(2, $attempt - 1), $MaxDelay)
                            $jitter = Get-Random -Minimum 0.0 -Maximum ($exp * 0.3)
                            [math]::Round($exp + $jitter, 1)
                        }
                        Start-Sleep -Seconds $delay
                        continue
                    }

                    # Non-retryable or retries exhausted
                    throw
                }
            }
        }

        $processed = 0
        $failed = 0
        $errors = [System.Collections.Generic.List[string]]::new()
        $processedIds = [System.Collections.Generic.List[string]]::new()

        $statsWriter = [System.IO.StreamWriter]::new($StatsFile, $false, [System.Text.Encoding]::UTF8)
        $folderWriter = $null
        if ($IncludeFolderStats -and $FolderStatsFile) {
            $folderWriter = [System.IO.StreamWriter]::new($FolderStatsFile, $false, [System.Text.Encoding]::UTF8)
        }

        try {
            foreach ($id in $SliceIds) {
                try {
                    # Get-EXOMailboxStatistics
                    $stats = Invoke-WithRetry -Action {
                        Get-EXOMailboxStatistics -Identity $id -ErrorAction Stop
                    } -MaxRetries $MaxRetries -BaseDelay $BaseDelaySeconds -MaxDelay $MaxDelaySeconds

                    $json = $stats | ConvertTo-Json -Compress -Depth 5
                    $statsWriter.WriteLine($json)

                    # Get-EXOMailboxFolderStatistics (optional)
                    if ($IncludeFolderStats -and $folderWriter) {
                        $folderStats = Invoke-WithRetry -Action {
                            Get-EXOMailboxFolderStatistics -Identity $id -ErrorAction Stop
                        } -MaxRetries $MaxRetries -BaseDelay $BaseDelaySeconds -MaxDelay $MaxDelaySeconds

                        foreach ($folder in $folderStats) {
                            $folder | Add-Member -NotePropertyName 'MailboxId' -NotePropertyValue $id -Force
                            $folderJson = $folder | ConvertTo-Json -Compress -Depth 5
                            $folderWriter.WriteLine($folderJson)
                        }
                    }

                    $processed++
                    $processedIds.Add($id)

                    # Flush periodically
                    if ($processed % 100 -eq 0) {
                        $statsWriter.Flush()
                        if ($folderWriter) { $folderWriter.Flush() }
                    }
                }
                catch {
                    $failed++
                    $errorMsg = "Mailbox ${id}: $($_.Exception.Message)"
                    $errors.Add($errorMsg)
                    if ($errors.Count -le 100) {
                        Write-Warning "Chunk ${ChunkIndex}: $errorMsg"
                    }
                }
            }
        }
        finally {
            $statsWriter.Flush()
            $statsWriter.Close()
            $statsWriter.Dispose()
            if ($folderWriter) {
                $folderWriter.Flush()
                $folderWriter.Close()
                $folderWriter.Dispose()
            }
        }

        return [PSCustomObject]@{
            ChunkIndex   = $ChunkIndex
            Processed    = $processed
            Failed       = $failed
            Errors       = $errors.ToArray()
            ProcessedIds = $processedIds.ToArray()
        }
    }

    # Dispatch slices to runspaces
    $handles = @()
    for ($i = 0; $i -lt $poolSize; $i++) {
        if ($slices[$i].Count -eq 0) { continue }

        $chunkNum = ($i + 1).ToString('000')
        $statsFile = Join-Path $StatsDir "chunk-$chunkNum.jsonl"
        $folderStatsFile = if ($IncludeFolderStats) { Join-Path $FolderStatsDir "chunk-$chunkNum.jsonl" } else { '' }

        $ps = [PowerShell]::Create()
        $ps.RunspacePool = $Pool

        $ps.AddScript($statsScript).AddParameters(@{
            SliceIds            = $slices[$i].ToArray()
            StatsFile           = $statsFile
            FolderStatsFile     = $folderStatsFile
            IncludeFolderStats  = $IncludeFolderStats
            MaxRetries          = $MaxRetries
            BaseDelaySeconds    = $BaseDelaySeconds
            MaxDelaySeconds     = $MaxDelaySeconds
            ChunkIndex          = $i
        }) | Out-Null

        $handle = $ps.BeginInvoke()
        $handles += @{
            PowerShell = $ps
            Handle     = $handle
            Index      = $i
            SliceCount = $slices[$i].Count
        }

        Write-ExportLog "Dispatched chunk $chunkNum ($($slices[$i].Count) mailboxes) to runspace"
    }

    # Poll for completion
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $allProcessedIds = [System.Collections.Generic.List[string]]::new()
    $totalProcessed = 0
    $totalFailed = 0
    $allErrors = [System.Collections.Generic.List[string]]::new()
    $completedChunks = [System.Collections.Generic.HashSet[int]]::new()

    while ($script:Running -and $completedChunks.Count -lt $handles.Count) {
        foreach ($item in $handles) {
            if ($completedChunks.Contains($item.Index)) { continue }
            if (-not $item.Handle.IsCompleted) { continue }

            try {
                $output = $item.PowerShell.EndInvoke($item.Handle)

                if ($item.PowerShell.HadErrors) {
                    foreach ($err in $item.PowerShell.Streams.Error) {
                        Write-ExportLog "Chunk $($item.Index) error: $($err.Exception.Message)" -Severity Warning
                    }
                }

                if ($output -and $output.Count -gt 0) {
                    $result = $output[-1]
                    $totalProcessed += $result.Processed
                    $totalFailed += $result.Failed

                    if ($result.ProcessedIds) {
                        foreach ($pid in $result.ProcessedIds) {
                            $allProcessedIds.Add($pid)
                        }
                    }
                    if ($result.Errors) {
                        foreach ($errMsg in $result.Errors) {
                            $allErrors.Add($errMsg)
                        }
                    }

                    Write-ExportLog "Chunk $($item.Index) complete: $($result.Processed) processed, $($result.Failed) failed"
                }
            }
            catch {
                Write-ExportLog "Chunk $($item.Index) collection failed: $($_.Exception.Message)" -Severity Error
            }
            finally {
                $item.PowerShell.Dispose()
                $completedChunks.Add($item.Index) | Out-Null
            }

            # Save progress after each chunk completes
            Save-ExportProgress -Path $ProgressPath -ProcessedIds $allProcessedIds
        }

        if ($completedChunks.Count -lt $handles.Count) {
            Start-Sleep -Seconds 5
            $elapsed = $sw.Elapsed.ToString('hh\:mm\:ss')
            $pct = if ($totalCount -gt 0) { [math]::Round(($totalProcessed + $totalFailed) / $totalCount * 100, 1) } else { 0 }
            Write-ExportLog "Phase 2 progress: $totalProcessed/$totalCount processed ($pct%), $totalFailed failed, $($completedChunks.Count)/$($handles.Count) chunks done ($elapsed elapsed)"
        }
    }

    # If cancelled, save progress and clean up remaining handles
    if (-not $script:Running) {
        Write-ExportLog "Cancellation detected — saving progress..." -Severity Warning
        Save-ExportProgress -Path $ProgressPath -ProcessedIds $allProcessedIds
        foreach ($item in $handles) {
            if (-not $completedChunks.Contains($item.Index)) {
                try {
                    # Wait briefly for in-flight work to finish
                    if ($item.Handle.AsyncWaitHandle.WaitOne(30000)) {
                        $output = $item.PowerShell.EndInvoke($item.Handle)
                        if ($output -and $output.Count -gt 0) {
                            $result = $output[-1]
                            $totalProcessed += $result.Processed
                            $totalFailed += $result.Failed
                            if ($result.ProcessedIds) {
                                foreach ($pid in $result.ProcessedIds) {
                                    $allProcessedIds.Add($pid)
                                }
                            }
                        }
                    }
                }
                catch { }
                finally {
                    $item.PowerShell.Dispose()
                }
            }
        }
        Save-ExportProgress -Path $ProgressPath -ProcessedIds $allProcessedIds
    }

    $sw.Stop()
    Write-ExportLog "Phase 2 complete: $totalProcessed processed, $totalFailed failed in $($sw.Elapsed.ToString('hh\:mm\:ss'))"

    return @{
        Processed    = $totalProcessed
        Failed       = $totalFailed
        Errors       = $allErrors
        ProcessedIds = $allProcessedIds
        Duration     = $sw.Elapsed
    }
}

function Save-ExportProgress {
    <#
    .SYNOPSIS
        Atomically writes a progress checkpoint file.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]]$ProcessedIds
    )

    $progress = @{
        Timestamp    = (Get-Date -Format 'o')
        ProcessedIds = $ProcessedIds.ToArray()
        Count        = $ProcessedIds.Count
    }

    $json = $progress | ConvertTo-Json -Depth 3
    $tempPath = "$Path.tmp"
    [System.IO.File]::WriteAllText($tempPath, $json, [System.Text.Encoding]::UTF8)
    [System.IO.File]::Move($tempPath, $Path, $true)
}

function Read-ExportProgress {
    <#
    .SYNOPSIS
        Loads a progress checkpoint and returns the set of already-processed mailbox IDs.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RunDir
    )

    $progressFile = Join-Path $RunDir 'progress.json'
    if (-not (Test-Path $progressFile)) {
        return $null
    }

    $content = [System.IO.File]::ReadAllText($progressFile)
    $progress = $content | ConvertFrom-Json

    $processedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $progress.ProcessedIds) {
        $processedSet.Add($id) | Out-Null
    }

    Write-ExportLog "Loaded resume checkpoint: $($processedSet.Count) previously processed mailboxes"
    return $processedSet
}

function Close-ExportRunspacePool {
    <#
    .SYNOPSIS
        Gracefully closes and disposes the runspace pool.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Runspaces.RunspacePool]$Pool
    )

    Write-ExportLog "Closing runspace pool..."
    try {
        $Pool.Close()
        $Pool.Dispose()
        Write-ExportLog "Runspace pool closed."
    }
    catch {
        Write-ExportLog "Error closing runspace pool: $($_.Exception.Message)" -Severity Warning
    }
}

#endregion

#region Main execution

$overallStart = Get-Date
Write-ExportLog "=== EXO Mailbox Data Export ==="
Write-ExportLog "Organization: $Organization"
Write-ExportLog "MaxParallelism: $MaxParallelism"
Write-ExportLog "IncludeFolderStatistics: $IncludeFolderStatistics"
Write-ExportLog "Resume: $Resume"

# --- Load certificate ---
$certificate = Get-ExportCertificate -Path $CertificatePath -Password $CertificatePassword -Thumbprint $CertificateThumbprint

# --- Determine run directory ---
$runDir = $null
$resumeProcessedSet = $null

if ($Resume) {
    # Find the most recent run directory
    $outputRoot = Resolve-Path -Path $OutputPath -ErrorAction SilentlyContinue
    if ($outputRoot) {
        $existingRuns = Get-ChildItem -Path $outputRoot.Path -Directory | Sort-Object Name -Descending
        if ($existingRuns.Count -gt 0) {
            $runDir = $existingRuns[0].FullName
            Write-ExportLog "Resuming from: $runDir"
            $resumeProcessedSet = Read-ExportProgress -RunDir $runDir
        }
    }
}

if (-not $runDir) {
    if (-not (Test-Path $OutputPath)) {
        New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
    }
    $timestamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
    $runDir = Join-Path (Resolve-Path -Path $OutputPath).Path $timestamp
    New-Item -Path $runDir -ItemType Directory -Force | Out-Null
}

$statsDir = Join-Path $runDir 'mailbox-statistics'
$folderStatsDir = Join-Path $runDir 'mailbox-folder-statistics'

New-Item -Path $statsDir -ItemType Directory -Force | Out-Null
if ($IncludeFolderStatistics) {
    New-Item -Path $folderStatsDir -ItemType Directory -Force | Out-Null
}

$progressPath = Join-Path $runDir 'progress.json'
Write-ExportLog "Output directory: $runDir"

# --- Connect main thread to EXO (for Phase 1) ---
Write-ExportLog "Connecting main thread to Exchange Online..."
$connectParams = @{
    Certificate  = $certificate
    AppId        = $AppId
    Organization = $Organization
    ShowBanner   = $false
    ErrorAction  = 'Stop'
}
Connect-ExchangeOnline @connectParams
Write-ExportLog "Main thread connected to Exchange Online."

# --- Phase 1: Export mailboxes ---
$mailboxFile = Join-Path $runDir 'mailboxes.jsonl'
$phase1Result = Export-Mailboxes -FilePath $mailboxFile

if (-not $script:Running) {
    Write-ExportLog "Export cancelled during Phase 1." -Severity Warning
    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue
    exit 1
}

if ($phase1Result.Count -eq 0) {
    Write-ExportLog "No mailboxes found. Exiting."
    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue
    exit 0
}

# --- Prepare Phase 2 work queue ---
$workQueue = $phase1Result.MailboxIds

if ($resumeProcessedSet -and $resumeProcessedSet.Count -gt 0) {
    $filtered = [System.Collections.Generic.List[string]]::new()
    foreach ($id in $workQueue) {
        if (-not $resumeProcessedSet.Contains($id)) {
            $filtered.Add($id)
        }
    }
    Write-ExportLog "Resume: filtered $($workQueue.Count - $filtered.Count) already-processed mailboxes, $($filtered.Count) remaining"
    $workQueue = $filtered
}

if ($workQueue.Count -eq 0) {
    Write-ExportLog "All mailboxes already processed (resume). Skipping Phase 2."
}
else {
    # --- Disconnect main thread EXO (runspaces get their own connections) ---
    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue

    # --- Initialize RunspacePool ---
    $certBytes = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
    $pool = $null

    try {
        $pool = Initialize-ExportRunspacePool -PoolSize $MaxParallelism -AppId $AppId -Organization $Organization -CertificateBytes $certBytes

        # Zero cert bytes now that runspaces have copies
        [Array]::Clear($certBytes, 0, $certBytes.Length)

        # --- Phase 2: Export statistics ---
        $phase2Result = Export-MailboxStatistics `
            -Pool $pool `
            -MailboxIds $workQueue `
            -StatsDir $statsDir `
            -FolderStatsDir $folderStatsDir `
            -IncludeFolderStats $IncludeFolderStatistics.IsPresent `
            -MaxRetries $MaxRetries `
            -BaseDelaySeconds $BaseDelaySeconds `
            -MaxDelaySeconds $MaxDelaySeconds `
            -ProgressPath $progressPath
    }
    finally {
        if ($pool) {
            Close-ExportRunspacePool -Pool $pool
        }
    }
}

# --- Write export summary ---
$overallEnd = Get-Date
$overallDuration = $overallEnd - $overallStart

$summary = [ordered]@{
    ExportTimestamp         = $overallStart.ToString('o')
    Organization           = $Organization
    IncludeFolderStatistics = $IncludeFolderStatistics.IsPresent
    MaxParallelism         = $MaxParallelism
    Resumed                = $Resume.IsPresent
    Phase1                 = [ordered]@{
        MailboxCount = $phase1Result.Count
        Duration     = $phase1Result.Duration.ToString()
    }
    Phase2                 = if ($workQueue.Count -gt 0 -and $phase2Result) {
        [ordered]@{
            Processed  = $phase2Result.Processed
            Failed     = $phase2Result.Failed
            ErrorCount = $phase2Result.Errors.Count
            Duration   = $phase2Result.Duration.ToString()
            Errors     = if ($phase2Result.Errors.Count -le 50) {
                $phase2Result.Errors.ToArray()
            }
            else {
                $phase2Result.Errors.GetRange(0, 50).ToArray() + @("... and $($phase2Result.Errors.Count - 50) more")
            }
        }
    }
    else {
        [ordered]@{ Skipped = $true }
    }
    TotalDuration          = $overallDuration.ToString()
    OutputDirectory        = $runDir
}

$summaryPath = Join-Path $runDir 'export-summary.json'
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding UTF8
Write-ExportLog "Export summary written to: $summaryPath"

# --- Final report ---
Write-ExportLog "=== Export Complete ==="
Write-ExportLog "Mailboxes: $($phase1Result.Count)"
if ($workQueue.Count -gt 0 -and $phase2Result) {
    Write-ExportLog "Statistics: $($phase2Result.Processed) processed, $($phase2Result.Failed) failed"
}
Write-ExportLog "Total duration: $($overallDuration.ToString('hh\:mm\:ss'))"
Write-ExportLog "Output: $runDir"

# Clean up
[Console]::remove_CancelKeyPress($cancelHandler)

#endregion
