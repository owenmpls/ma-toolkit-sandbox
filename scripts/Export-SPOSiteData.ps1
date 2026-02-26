<#
.SYNOPSIS
    Bulk-exports SharePoint Online and OneDrive site data to JSONL for Databricks ingestion.
.DESCRIPTION
    Exports site collection metadata, storage metrics, permissions, and document library
    inventories for an entire tenant. Designed for pre-migration assessment of cross-tenant
    SharePoint/OneDrive migrations (1M item / 2TB-5TB limits).

    Phase 1 streams Get-PnPTenantSite to a single JSONL file (serial, admin connection).
    Phase 2 uses a RunspacePool to connect per-site and collect enriched data in parallel,
    writing per-runspace chunk files.

    Output structure:
      {OutputPath}/YYYY-MM-DD_HHmmss/
        sites.jsonl
        site-details/chunk-001.jsonl ...
        progress.json
        export-summary.json

    Requires PnP.PowerShell module and an Entra ID app registration with:
      - SharePoint > Sites.FullControl.All (Application)
      - Microsoft Graph > Sites.Read.All (Application)
      - Microsoft Graph > GroupMember.Read.All (Application)
.EXAMPLE
    .\Export-SPOSiteData.ps1 -AdminUrl "https://contoso-admin.sharepoint.com" `
        -AppId "00000000-..." -TenantDomain "contoso.onmicrosoft.com" `
        -CertificatePath .\cert.pfx -CertificatePassword (Read-Host -AsSecureString) `
        -MaxParallelism 10
.EXAMPLE
    .\Export-SPOSiteData.ps1 -AdminUrl "https://contoso-admin.sharepoint.com" `
        -AppId "00000000-..." -TenantDomain "contoso.onmicrosoft.com" `
        -CertificateThumbprint "ABC123..." -IncludeSharingLinks -Resume
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AdminUrl,

    [Parameter(Mandatory)]
    [string]$AppId,

    [Parameter(Mandatory)]
    [string]$TenantDomain,

    [Parameter(Mandatory, ParameterSetName = 'PFXFile')]
    [string]$CertificatePath,

    [Parameter(Mandatory, ParameterSetName = 'PFXFile')]
    [SecureString]$CertificatePassword,

    [Parameter(Mandatory, ParameterSetName = 'Thumbprint')]
    [string]$CertificateThumbprint,

    [string]$OutputPath = './spo-export',

    [switch]$IncludeSharingLinks,

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

function Export-Sites {
    <#
    .SYNOPSIS
        Phase 1: Streams Get-PnPTenantSite to a JSONL file, returns the list of site URLs.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$FilePath
    )

    Write-ExportLog "Phase 1: Exporting tenant sites to $FilePath"
    $siteUrls = [System.Collections.Generic.List[string]]::new()
    $count = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # StreamWriter for efficient line-by-line append
    $writer = [System.IO.StreamWriter]::new($FilePath, $false, [System.Text.Encoding]::UTF8)
    try {
        Get-PnPTenantSite -IncludeOneDriveSites -Detailed | ForEach-Object {
            if (-not $script:Running) { return }

            $json = $_ | ConvertTo-Json -Compress -Depth 5
            $writer.WriteLine($json)
            $siteUrls.Add($_.Url)
            $count++

            if ($count % 500 -eq 0) {
                $writer.Flush()
                $elapsed = $sw.Elapsed.ToString('hh\:mm\:ss')
                Write-ExportLog "Phase 1: $count sites exported ($elapsed elapsed)"
            }
        }
    }
    finally {
        $writer.Flush()
        $writer.Close()
        $writer.Dispose()
    }

    $sw.Stop()
    Write-ExportLog "Phase 1 complete: $count sites exported in $($sw.Elapsed.ToString('hh\:mm\:ss'))"

    return @{
        SiteUrls = $siteUrls
        Count    = $count
        Duration = $sw.Elapsed
    }
}

function Initialize-ExportRunspacePool {
    <#
    .SYNOPSIS
        Creates a RunspacePool with PnP.PowerShell imported. Validates module load and
        stores auth config as globals. No pre-authentication — PnP connects per-site.
    #>
    param(
        [Parameter(Mandatory)]
        [int]$PoolSize,

        [Parameter(Mandatory)]
        [string]$AppId,

        [Parameter(Mandatory)]
        [string]$TenantDomain,

        [Parameter(Mandatory)]
        [string]$CertificateBase64,

        [Parameter(Mandatory)]
        [bool]$IncludeSharingLinks
    )

    Write-ExportLog "Initializing runspace pool (size=$PoolSize)..."

    $initialState = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    $initialState.ImportPSModule('PnP.PowerShell')

    $pool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(
        1,
        $PoolSize,
        $initialState,
        (Get-Host)
    )
    $pool.Open()

    # Validate module load + store auth config in each runspace
    $initScript = {
        param($AppId, $TenantDomain, $CertificateBase64, $IncludeSharingLinks, $RunspaceIndex)

        # Validate PnP.PowerShell is available
        $mod = Get-Module -Name PnP.PowerShell -ErrorAction SilentlyContinue
        if (-not $mod) {
            throw "PnP.PowerShell module not loaded in runspace $RunspaceIndex"
        }

        # Store auth config for per-site connections
        $global:ExportAuthConfig = @{
            AppId              = $AppId
            TenantDomain       = $TenantDomain
            CertificateBase64  = $CertificateBase64
            IncludeSharingLinks = $IncludeSharingLinks
        }

        return "Runspace ${RunspaceIndex}: PnP.PowerShell loaded (v$($mod.Version))."
    }

    $initHandles = @()
    for ($i = 0; $i -lt $PoolSize; $i++) {
        $ps = [PowerShell]::Create()
        $ps.RunspacePool = $pool

        $ps.AddScript($initScript).AddParameters(@{
            AppId              = $AppId
            TenantDomain       = $TenantDomain
            CertificateBase64  = $CertificateBase64
            IncludeSharingLinks = $IncludeSharingLinks
            RunspaceIndex      = $i
        }) | Out-Null

        $handle = $ps.BeginInvoke()
        $initHandles += @{
            PowerShell = $ps
            Handle     = $handle
            Index      = $i
        }
    }

    $failedCount = 0
    foreach ($item in $initHandles) {
        try {
            $result = $item.PowerShell.EndInvoke($item.Handle)
            Write-ExportLog "$result"

            if ($item.PowerShell.HadErrors) {
                foreach ($err in $item.PowerShell.Streams.Error) {
                    Write-ExportLog "Runspace $($item.Index) init warning: $($err.Exception.Message)" -Severity Warning
                }
            }
        }
        catch {
            $failedCount++
            Write-ExportLog "Runspace $($item.Index) init failed: $($_.Exception.Message)" -Severity Error
        }
        finally {
            $item.PowerShell.Dispose()
        }
    }

    if ($failedCount -eq $PoolSize) {
        $pool.Close()
        $pool.Dispose()
        throw "All runspaces failed to initialize. Cannot proceed."
    }

    if ($failedCount -gt 0) {
        Write-ExportLog "$failedCount runspace(s) failed init. Running with reduced parallelism." -Severity Warning
    }

    Write-ExportLog "Runspace pool ready ($($PoolSize - $failedCount)/$PoolSize initialized)."
    return $pool
}

function Export-SiteDetails {
    <#
    .SYNOPSIS
        Phase 2: Partitions site URLs across runspaces and exports per-site details in parallel.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Runspaces.RunspacePool]$Pool,

        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]]$SiteUrls,

        [Parameter(Mandatory)]
        [string]$DetailsDir,

        [Parameter(Mandatory)]
        [int]$MaxRetries,

        [Parameter(Mandatory)]
        [int]$BaseDelaySeconds,

        [Parameter(Mandatory)]
        [int]$MaxDelaySeconds,

        [Parameter(Mandatory)]
        [string]$ProgressPath
    )

    $totalCount = $SiteUrls.Count
    $poolSize = $Pool.GetMaxRunspaces()
    Write-ExportLog "Phase 2: Exporting site details for $totalCount sites across $poolSize runspaces"

    # Partition site URLs into equal slices (round-robin)
    $slices = @()
    for ($i = 0; $i -lt $poolSize; $i++) {
        $slices += , [System.Collections.Generic.List[string]]::new()
    }
    for ($i = 0; $i -lt $totalCount; $i++) {
        $sliceIndex = $i % $poolSize
        $slices[$sliceIndex].Add($SiteUrls[$i])
    }

    # Scriptblock that runs inside each runspace
    $detailsScript = {
        param(
            [string[]]$SliceUrls,
            [string]$DetailsFile,
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

                    # Skippable errors — 403/404/locked sites
                    foreach ($pattern in @('403', 'Forbidden', 'Access denied', '404', 'Not Found', 'does not exist', 'site.*locked')) {
                        if ($matchText -match $pattern) {
                            # Re-throw with a marker so the caller can detect skip
                            throw [System.InvalidOperationException]::new("SKIPPABLE: $($ex.Message)", $ex)
                        }
                    }

                    # Auth error — reconnect and retry
                    $isAuthError = $false
                    foreach ($pattern in @('401', 'Unauthorized', 'token.*expired', 'ACS50012', 'Access token has expired')) {
                        if ($matchText -match $pattern) { $isAuthError = $true; break }
                    }
                    if ($isAuthError -and $global:ExportAuthConfig -and $attempt -le $MaxRetries) {
                        try {
                            $cfg = $global:ExportAuthConfig
                            $pnpParams = @{
                                Url                    = $global:CurrentSiteUrl
                                ClientId               = $cfg.AppId
                                Tenant                 = $cfg.TenantDomain
                                CertificateBase64Encoded = $cfg.CertificateBase64
                                ErrorAction            = 'Stop'
                            }
                            Connect-PnPOnline @pnpParams
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
                        'Request is throttled', 'exceeded.*throttl', 'rate.*limit', 'please.*retry'
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

        # Inline per-site data collection helper
        function Collect-SiteData {
            param(
                [string]$SiteUrl,
                [bool]$IncludeSharingLinks
            )

            $record = [ordered]@{
                SiteUrl   = $SiteUrl
                Skipped   = $false
                Timestamp = (Get-Date -Format 'o')
            }

            # 1. Storage metrics
            try {
                $storageMetric = Get-PnPFolderStorageMetric -FolderSiteRelativeUrl "/" -ErrorAction Stop
                $record['StorageMetrics'] = [ordered]@{
                    TotalFileCount      = $storageMetric.TotalFileCount
                    TotalFileStreamSize = $storageMetric.TotalFileStreamSize
                    TotalSize           = $storageMetric.TotalSize
                }
            }
            catch {
                $record['StorageMetrics'] = $null
                $record['StorageMetricsError'] = $_.Exception.Message
            }

            # 2. Sensitivity label
            try {
                $label = Get-PnPSiteSensitivityLabel -ErrorAction Stop
                $record['SensitivityLabel'] = $label
            }
            catch {
                $record['SensitivityLabel'] = $null
            }

            # 3. Site collection admins
            try {
                $admins = Get-PnPSiteCollectionAdmin -ErrorAction Stop
                $record['SiteCollectionAdmins'] = @($admins | ForEach-Object {
                    [ordered]@{
                        LoginName = $_.LoginName
                        Title     = $_.Title
                        Email     = $_.Email
                    }
                })
            }
            catch {
                $record['SiteCollectionAdmins'] = @()
                $record['SiteCollectionAdminsError'] = $_.Exception.Message
            }

            # 4. SharePoint groups + members
            try {
                $groups = Get-PnPGroup -ErrorAction Stop
                $record['Groups'] = @($groups | ForEach-Object {
                    $group = $_
                    $members = @()
                    try {
                        $members = @(Get-PnPGroupMember -Group $group.Title -ErrorAction Stop | ForEach-Object {
                            [ordered]@{
                                LoginName = $_.LoginName
                                Title     = $_.Title
                                Email     = $_.Email
                            }
                        })
                    }
                    catch { }
                    [ordered]@{
                        Id      = $group.Id
                        Title   = $group.Title
                        Owner   = $group.OwnerTitle
                        Members = $members
                    }
                })
            }
            catch {
                $record['Groups'] = @()
                $record['GroupsError'] = $_.Exception.Message
            }

            # 5. Document library inventory (BaseTemplate: 101=DocLib, 700=OneDriveLib, 119=WikiPages)
            try {
                $lists = Get-PnPList -ErrorAction Stop | Where-Object {
                    $_.BaseTemplate -in @(101, 700, 119)
                }
                $record['DocumentLibraries'] = @($lists | ForEach-Object {
                    [ordered]@{
                        Title        = $_.Title
                        Id           = $_.Id.ToString()
                        BaseTemplate = $_.BaseTemplate
                        ItemCount    = $_.ItemCount
                        Created      = $_.Created.ToString('o')
                        LastModified = $_.LastItemUserModifiedDate.ToString('o')
                    }
                })
            }
            catch {
                $record['DocumentLibraries'] = @()
                $record['DocumentLibrariesError'] = $_.Exception.Message
            }

            # 6. Sharing links (opt-in — expensive)
            if ($IncludeSharingLinks) {
                try {
                    $sharingLinks = @()
                    foreach ($lib in $record['DocumentLibraries']) {
                        try {
                            $links = Get-PnPFolderSharingLink -Folder $lib.Title -ErrorAction Stop
                            foreach ($link in $links) {
                                $sharingLinks += [ordered]@{
                                    Library = $lib.Title
                                    Link    = $link
                                }
                            }
                        }
                        catch { }
                    }
                    $record['SharingLinks'] = $sharingLinks
                }
                catch {
                    $record['SharingLinks'] = @()
                    $record['SharingLinksError'] = $_.Exception.Message
                }
            }
            else {
                $record['SharingLinks'] = $null
            }

            return $record
        }

        $processed = 0
        $failed = 0
        $skipped = 0
        $errors = [System.Collections.Generic.List[string]]::new()
        $processedUrls = [System.Collections.Generic.List[string]]::new()

        $cfg = $global:ExportAuthConfig
        $detailsWriter = [System.IO.StreamWriter]::new($DetailsFile, $false, [System.Text.Encoding]::UTF8)

        try {
            foreach ($siteUrl in $SliceUrls) {
                $global:CurrentSiteUrl = $siteUrl

                try {
                    # Connect to site
                    Invoke-WithRetry -Action {
                        $pnpParams = @{
                            Url                    = $siteUrl
                            ClientId               = $cfg.AppId
                            Tenant                 = $cfg.TenantDomain
                            CertificateBase64Encoded = $cfg.CertificateBase64
                            ErrorAction            = 'Stop'
                        }
                        Connect-PnPOnline @pnpParams
                    } -MaxRetries $MaxRetries -BaseDelay $BaseDelaySeconds -MaxDelay $MaxDelaySeconds

                    # Collect site data
                    $record = Invoke-WithRetry -Action {
                        Collect-SiteData -SiteUrl $siteUrl -IncludeSharingLinks $cfg.IncludeSharingLinks
                    } -MaxRetries $MaxRetries -BaseDelay $BaseDelaySeconds -MaxDelay $MaxDelaySeconds

                    $json = $record | ConvertTo-Json -Compress -Depth 10
                    $detailsWriter.WriteLine($json)
                    $processed++
                    $processedUrls.Add($siteUrl)
                }
                catch {
                    $exMessage = $_.Exception.Message
                    if ($exMessage -like 'SKIPPABLE:*') {
                        # Write skip record for 403/404/locked sites
                        $skipRecord = [ordered]@{
                            SiteUrl    = $siteUrl
                            Skipped    = $true
                            SkipReason = $exMessage -replace '^SKIPPABLE:\s*', ''
                            Timestamp  = (Get-Date -Format 'o')
                        }
                        $json = $skipRecord | ConvertTo-Json -Compress -Depth 3
                        $detailsWriter.WriteLine($json)
                        $skipped++
                        $processedUrls.Add($siteUrl)
                    }
                    else {
                        $failed++
                        $errorMsg = "${siteUrl}: $exMessage"
                        $errors.Add($errorMsg)
                        if ($errors.Count -le 100) {
                            Write-Warning "Chunk ${ChunkIndex}: $errorMsg"
                        }
                    }
                }
                finally {
                    # Disconnect to prevent connection object accumulation
                    try { Disconnect-PnPOnline -ErrorAction SilentlyContinue } catch { }
                }

                # Flush periodically
                if (($processed + $skipped + $failed) % 50 -eq 0) {
                    $detailsWriter.Flush()
                }
            }
        }
        finally {
            $detailsWriter.Flush()
            $detailsWriter.Close()
            $detailsWriter.Dispose()
        }

        return [PSCustomObject]@{
            ChunkIndex    = $ChunkIndex
            Processed     = $processed
            Skipped       = $skipped
            Failed        = $failed
            Errors        = $errors.ToArray()
            ProcessedUrls = $processedUrls.ToArray()
        }
    }

    # Dispatch slices to runspaces
    $handles = @()
    for ($i = 0; $i -lt $poolSize; $i++) {
        if ($slices[$i].Count -eq 0) { continue }

        $chunkNum = ($i + 1).ToString('000')
        $detailsFile = Join-Path $DetailsDir "chunk-$chunkNum.jsonl"

        $ps = [PowerShell]::Create()
        $ps.RunspacePool = $Pool

        $ps.AddScript($detailsScript).AddParameters(@{
            SliceUrls       = $slices[$i].ToArray()
            DetailsFile     = $detailsFile
            MaxRetries      = $MaxRetries
            BaseDelaySeconds = $BaseDelaySeconds
            MaxDelaySeconds = $MaxDelaySeconds
            ChunkIndex      = $i
        }) | Out-Null

        $handle = $ps.BeginInvoke()
        $handles += @{
            PowerShell = $ps
            Handle     = $handle
            Index      = $i
            SliceCount = $slices[$i].Count
        }

        Write-ExportLog "Dispatched chunk $chunkNum ($($slices[$i].Count) sites) to runspace"
    }

    # Poll for completion
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $allProcessedUrls = [System.Collections.Generic.List[string]]::new()
    $totalProcessed = 0
    $totalSkipped = 0
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
                    $totalSkipped += $result.Skipped
                    $totalFailed += $result.Failed

                    if ($result.ProcessedUrls) {
                        foreach ($url in $result.ProcessedUrls) {
                            $allProcessedUrls.Add($url)
                        }
                    }
                    if ($result.Errors) {
                        foreach ($errMsg in $result.Errors) {
                            $allErrors.Add($errMsg)
                        }
                    }

                    Write-ExportLog "Chunk $($item.Index) complete: $($result.Processed) processed, $($result.Skipped) skipped, $($result.Failed) failed"
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
            Save-ExportProgress -Path $ProgressPath -ProcessedUrls $allProcessedUrls
        }

        if ($completedChunks.Count -lt $handles.Count) {
            Start-Sleep -Seconds 5
            $elapsed = $sw.Elapsed.ToString('hh\:mm\:ss')
            $pct = if ($totalCount -gt 0) { [math]::Round(($totalProcessed + $totalSkipped + $totalFailed) / $totalCount * 100, 1) } else { 0 }
            Write-ExportLog "Phase 2 progress: $totalProcessed/$totalCount processed, $totalSkipped skipped, $totalFailed failed ($pct%, $($completedChunks.Count)/$($handles.Count) chunks done, $elapsed elapsed)"
        }
    }

    # If cancelled, save progress and clean up remaining handles
    if (-not $script:Running) {
        Write-ExportLog "Cancellation detected — saving progress..." -Severity Warning
        Save-ExportProgress -Path $ProgressPath -ProcessedUrls $allProcessedUrls
        foreach ($item in $handles) {
            if (-not $completedChunks.Contains($item.Index)) {
                try {
                    # Wait briefly for in-flight work to finish
                    if ($item.Handle.AsyncWaitHandle.WaitOne(30000)) {
                        $output = $item.PowerShell.EndInvoke($item.Handle)
                        if ($output -and $output.Count -gt 0) {
                            $result = $output[-1]
                            $totalProcessed += $result.Processed
                            $totalSkipped += $result.Skipped
                            $totalFailed += $result.Failed
                            if ($result.ProcessedUrls) {
                                foreach ($url in $result.ProcessedUrls) {
                                    $allProcessedUrls.Add($url)
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
        Save-ExportProgress -Path $ProgressPath -ProcessedUrls $allProcessedUrls
    }

    $sw.Stop()
    Write-ExportLog "Phase 2 complete: $totalProcessed processed, $totalSkipped skipped, $totalFailed failed in $($sw.Elapsed.ToString('hh\:mm\:ss'))"

    return @{
        Processed     = $totalProcessed
        Skipped       = $totalSkipped
        Failed        = $totalFailed
        Errors        = $allErrors
        ProcessedUrls = $allProcessedUrls
        Duration      = $sw.Elapsed
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
        [System.Collections.Generic.List[string]]$ProcessedUrls
    )

    $progress = @{
        Timestamp     = (Get-Date -Format 'o')
        ProcessedUrls = $ProcessedUrls.ToArray()
        Count         = $ProcessedUrls.Count
    }

    $json = $progress | ConvertTo-Json -Depth 3
    $tempPath = "$Path.tmp"
    [System.IO.File]::WriteAllText($tempPath, $json, [System.Text.Encoding]::UTF8)
    [System.IO.File]::Move($tempPath, $Path, $true)
}

function Read-ExportProgress {
    <#
    .SYNOPSIS
        Loads a progress checkpoint and returns the set of already-processed site URLs.
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
    foreach ($url in $progress.ProcessedUrls) {
        $processedSet.Add($url) | Out-Null
    }

    Write-ExportLog "Loaded resume checkpoint: $($processedSet.Count) previously processed sites"
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
Write-ExportLog "=== SPO Site Data Export ==="
Write-ExportLog "AdminUrl: $AdminUrl"
Write-ExportLog "TenantDomain: $TenantDomain"
Write-ExportLog "MaxParallelism: $MaxParallelism"
Write-ExportLog "IncludeSharingLinks: $IncludeSharingLinks"
Write-ExportLog "Resume: $Resume"

# --- Load certificate ---
$certificate = Get-ExportCertificate -Path $CertificatePath -Password $CertificatePassword -Thumbprint $CertificateThumbprint

# --- Export PFX as Base64 for runspace auth (PnP accepts -CertificateBase64Encoded) ---
$certBase64 = [System.Convert]::ToBase64String(
    $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
)

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

$detailsDir = Join-Path $runDir 'site-details'
New-Item -Path $detailsDir -ItemType Directory -Force | Out-Null

$progressPath = Join-Path $runDir 'progress.json'
Write-ExportLog "Output directory: $runDir"

# --- Connect main thread to SPO admin center (for Phase 1) ---
Write-ExportLog "Connecting main thread to SPO admin center..."
$connectParams = @{
    Url                    = $AdminUrl
    ClientId               = $AppId
    Tenant                 = $TenantDomain
    CertificateBase64Encoded = $certBase64
    ErrorAction            = 'Stop'
}
Connect-PnPOnline @connectParams
Write-ExportLog "Main thread connected to SPO admin center."

# --- Phase 1: Export sites ---
$sitesFile = Join-Path $runDir 'sites.jsonl'
$phase1Result = Export-Sites -FilePath $sitesFile

if (-not $script:Running) {
    Write-ExportLog "Export cancelled during Phase 1." -Severity Warning
    Disconnect-PnPOnline -ErrorAction SilentlyContinue
    exit 1
}

if ($phase1Result.Count -eq 0) {
    Write-ExportLog "No sites found. Exiting."
    Disconnect-PnPOnline -ErrorAction SilentlyContinue
    exit 0
}

# --- Prepare Phase 2 work queue ---
$workQueue = $phase1Result.SiteUrls

if ($resumeProcessedSet -and $resumeProcessedSet.Count -gt 0) {
    $filtered = [System.Collections.Generic.List[string]]::new()
    foreach ($url in $workQueue) {
        if (-not $resumeProcessedSet.Contains($url)) {
            $filtered.Add($url)
        }
    }
    Write-ExportLog "Resume: filtered $($workQueue.Count - $filtered.Count) already-processed sites, $($filtered.Count) remaining"
    $workQueue = $filtered
}

if ($workQueue.Count -eq 0) {
    Write-ExportLog "All sites already processed (resume). Skipping Phase 2."
}
else {
    # --- Disconnect main thread PnP (runspaces connect per-site) ---
    Disconnect-PnPOnline -ErrorAction SilentlyContinue

    # --- Initialize RunspacePool ---
    $pool = $null

    try {
        $pool = Initialize-ExportRunspacePool `
            -PoolSize $MaxParallelism `
            -AppId $AppId `
            -TenantDomain $TenantDomain `
            -CertificateBase64 $certBase64 `
            -IncludeSharingLinks $IncludeSharingLinks.IsPresent

        # --- Phase 2: Export site details ---
        $phase2Result = Export-SiteDetails `
            -Pool $pool `
            -SiteUrls $workQueue `
            -DetailsDir $detailsDir `
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

# --- Zero cert Base64 ---
$certBase64 = $null

# --- Write export summary ---
$overallEnd = Get-Date
$overallDuration = $overallEnd - $overallStart

$summary = [ordered]@{
    ExportTimestamp      = $overallStart.ToString('o')
    AdminUrl             = $AdminUrl
    TenantDomain         = $TenantDomain
    IncludeSharingLinks  = $IncludeSharingLinks.IsPresent
    MaxParallelism       = $MaxParallelism
    Resumed              = $Resume.IsPresent
    Phase1               = [ordered]@{
        SiteCount = $phase1Result.Count
        Duration  = $phase1Result.Duration.ToString()
    }
    Phase2               = if ($workQueue.Count -gt 0 -and $phase2Result) {
        [ordered]@{
            Processed  = $phase2Result.Processed
            Skipped    = $phase2Result.Skipped
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
    TotalDuration        = $overallDuration.ToString()
    OutputDirectory      = $runDir
}

$summaryPath = Join-Path $runDir 'export-summary.json'
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding UTF8
Write-ExportLog "Export summary written to: $summaryPath"

# --- Final report ---
Write-ExportLog "=== Export Complete ==="
Write-ExportLog "Sites: $($phase1Result.Count)"
if ($workQueue.Count -gt 0 -and $phase2Result) {
    Write-ExportLog "Details: $($phase2Result.Processed) processed, $($phase2Result.Skipped) skipped, $($phase2Result.Failed) failed"
}
Write-ExportLog "Total duration: $($overallDuration.ToString('hh\:mm\:ss'))"
Write-ExportLog "Output: $runDir"

# Clean up
[Console]::remove_CancelKeyPress($cancelHandler)

#endregion
