function New-WorkerPool {
    param(
        [Parameter(Mandatory)][string]$ModuleName,
        [Parameter(Mandatory)][int]$PoolSize,
        [Parameter(Mandatory)][hashtable]$AuthConfig,
        [byte[]]$CertBytes = $null,
        [switch]$SkipPreAuth,
        [switch]$IncludeRetryHelper
    )

    $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    $iss.ImportPSModule($ModuleName)
    if ($IncludeRetryHelper) {
        $retryHelperPath = Join-Path $PSScriptRoot 'RetryHelper.psm1'
        $iss.ImportPSModule($retryHelperPath)
    }

    $pool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(
        $PoolSize, $PoolSize, $iss, (Get-Host)
    )
    $pool.Open()

    if (-not $SkipPreAuth -and $CertBytes) {
        $authHandles = @()
        for ($i = 0; $i -lt $PoolSize; $i++) {
            $ps = [PowerShell]::Create().AddScript({
                param($Config, $CertBytes)
                $global:ExportAuthConfig = $Config
                $global:ExportCertBytes = $CertBytes
            }).AddArgument($AuthConfig).AddArgument($CertBytes)

            $ps.RunspacePool = $pool
            $authHandles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke() }
        }

        foreach ($item in $authHandles) {
            $item.PowerShell.EndInvoke($item.Handle)
            $item.PowerShell.Dispose()
        }
    }

    return $pool
}

function Split-WorkItems {
    param(
        [Parameter(Mandatory)][string[]]$Items,
        [Parameter(Mandatory)][int]$SliceCount
    )

    $slices = @()
    for ($i = 0; $i -lt $SliceCount; $i++) {
        $slices += , [System.Collections.Generic.List[string]]::new()
    }

    for ($i = 0; $i -lt $Items.Count; $i++) {
        $sliceIndex = $i % $SliceCount
        $slices[$sliceIndex].Add($Items[$i])
    }

    return $slices
}

function Invoke-ParallelWork {
    param(
        [Parameter(Mandatory)][System.Management.Automation.Runspaces.RunspacePool]$Pool,
        [Parameter(Mandatory)][string[]]$Items,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][scriptblock]$WorkScript,
        [int]$PoolSize = 0
    )

    if ($PoolSize -eq 0) { $PoolSize = $Pool.GetMaxRunspaces() }
    $slices = Split-WorkItems -Items $Items -SliceCount $PoolSize

    $handles = @()
    for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
        $ps = [PowerShell]::Create().AddScript({
            param($Ids, $OutputDir, $ChunkNum, $RunId, $WorkScriptStr)

            $workFn = [scriptblock]::Create($WorkScriptStr)
            $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
            $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
            $processed = 0

            try {
                foreach ($id in $Ids) {
                    $result = & $workFn $id
                    if ($result) {
                        $writer.WriteLine(($result | ConvertTo-Json -Compress -Depth 5))
                        $processed++
                        if ($processed % 50 -eq 0) { $writer.Flush() }
                    }
                }
            }
            finally {
                $writer.Flush()
                $writer.Dispose()
            }

            return @{ ChunkIndex = $ChunkNum; Processed = $processed }
        }).AddArgument($slices[$chunkIndex]).AddArgument($OutputDirectory).AddArgument($chunkIndex).AddArgument($RunId).AddArgument($WorkScript.ToString())

        $ps.RunspacePool = $Pool
        $handles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke(); ChunkIndex = $chunkIndex }
    }

    # Poll for completion
    $completed = [System.Collections.Generic.HashSet[int]]::new()
    $totalProcessed = 0
    while ($completed.Count -lt $handles.Count) {
        foreach ($item in $handles) {
            if ($completed.Contains($item.ChunkIndex)) { continue }
            if ($item.Handle.IsCompleted) {
                $output = $item.PowerShell.EndInvoke($item.Handle)
                $item.PowerShell.Dispose()
                $completed.Add($item.ChunkIndex) | Out-Null
                if ($output -and $output.Processed) { $totalProcessed += $output.Processed }
            }
        }
        if ($completed.Count -lt $handles.Count) { Start-Sleep -Seconds 5 }
    }

    return @{ RecordCount = $totalProcessed; ChunkCount = $slices.Count }
}

# --- Auth/reconnect script templates per API family ---

$script:ModuleNames = @{
    graph = 'Microsoft.Graph.Authentication'
    exo   = 'ExchangeOnlineManagement'
    spo   = 'PnP.PowerShell'
}

$script:AuthScripts = @{
    graph = @'
param($Config, $Bytes, $Idx)
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $Bytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)
Connect-MgGraph -ClientId $Config.ClientId -TenantId $Config.TenantId `
    -Certificate $cert -NoWelcome -ErrorAction Stop
$global:IngestAuthConfig = $Config
$global:IngestCertBytes = [byte[]]$Bytes.Clone()
'@
    exo = @'
param($Config, $Bytes, $Idx)
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $Bytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)
$params = @{
    Certificate  = $cert
    AppId        = $Config.ClientId
    Organization = $Config.Organization
    ShowBanner   = $false
    ErrorAction  = 'Stop'
}
Connect-ExchangeOnline @params
$global:IngestAuthConfig = $Config
$global:IngestCertBytes = [byte[]]$Bytes.Clone()
'@
    spo = @'
param($Config, $Bytes, $Idx)
$global:IngestAuthConfig = $Config
Connect-PnPOnline -Url $Config.AdminUrl `
    -ClientId $Config.ClientId `
    -Tenant $Config.TenantDomain `
    -CertificateBase64Encoded $Config.CertificateBase64 `
    -ErrorAction Stop
'@
}

$script:ReconnectScripts = @{
    graph = @'
$cfg = $global:IngestAuthConfig
$bytes = $global:IngestCertBytes
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $bytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)
Connect-MgGraph -ClientId $cfg.ClientId -TenantId $cfg.TenantId `
    -Certificate $cert -NoWelcome -ErrorAction Stop
'@
    exo = @'
$cfg = $global:IngestAuthConfig
$bytes = $global:IngestCertBytes
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $bytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
)
$params = @{
    Certificate  = $cert
    AppId        = $cfg.ClientId
    Organization = $cfg.Organization
    ShowBanner   = $false
    ErrorAction  = 'Stop'
}
Connect-ExchangeOnline @params
'@
    spo = $null  # SPO reconnects per-site inside the work function
}

# --- Dispatch template (single-quoted — no escaping) ---

$script:DispatchTemplate = @'
param($ItemIds, $OutputDir, $ChunkNum, $RunId, $ApiFamily,
      $WorkScriptStr, $OnSkipScriptStr, $ReconnectScriptStr,
      $FlushInterval, $JsonDepth, $AuthCfg, $PreFlightScriptStr)

$MaxRetries = 5
$workFn = [scriptblock]::Create($WorkScriptStr)
$onSkipFn = if ($OnSkipScriptStr) { [scriptblock]::Create($OnSkipScriptStr) } else { $null }
$reconnectFn = if ($ReconnectScriptStr) { [scriptblock]::Create($ReconnectScriptStr) } else { $null }

$preFlightResult = $null
if ($PreFlightScriptStr) {
    $pfFn = [scriptblock]::Create($PreFlightScriptStr)
    $preFlightResult = & $pfFn $AuthCfg
}

$chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
$writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
$processed = 0
$skipped = 0
$errors = [System.Collections.Generic.List[string]]::new()

try {
    foreach ($itemId in $ItemIds) {
        $attempt = 0
        $itemDone = $false

        while (-not $itemDone) {
            $attempt++

            try {
                $records = & $workFn $itemId $AuthCfg $preFlightResult
                if ($records) {
                    if ($records -is [hashtable] -or $records -is [System.Collections.Specialized.OrderedDictionary]) {
                        $records = @(,$records)
                    }
                    foreach ($rec in $records) {
                        $writer.WriteLine(($rec | ConvertTo-Json -Compress -Depth $JsonDepth))
                        $processed++
                    }
                }
                $itemDone = $true
            }
            catch {
                $class = Get-ErrorClassification -ErrorRecord $_ -ApiFamily $ApiFamily

                if ($class.Category -eq 'Skippable') {
                    if ($onSkipFn) {
                        $skipRecord = & $onSkipFn $itemId $class.Message $AuthCfg
                        if ($skipRecord) {
                            $writer.WriteLine(($skipRecord | ConvertTo-Json -Compress -Depth $JsonDepth))
                        }
                    }
                    $skipped++
                    $itemDone = $true
                    continue
                }

                if ($attempt -ge $MaxRetries) {
                    $errors.Add("item=$itemId attempt=${attempt}: $($class.Message)")
                    $skipped++
                    $itemDone = $true
                    continue
                }

                if ($class.Category -eq 'Auth') {
                    if ($reconnectFn) {
                        try { & $reconnectFn }
                        catch { Write-Warning "Reconnect failed (attempt $attempt): $($_.Exception.Message)" }
                    }
                    continue
                }

                if ($class.Category -eq 'Throttle') {
                    Start-Sleep -Seconds (Get-RetryDelay -Classification $class -Attempt $attempt)
                    continue
                }

                $errors.Add("item=$itemId attempt=${attempt}: $($class.Message)")
                $skipped++
                $itemDone = $true
            }
        }

        if ($FlushInterval -gt 0 -and $processed % $FlushInterval -eq 0 -and $processed -gt 0) {
            $writer.Flush()
        }
    }
}
finally {
    $writer.Flush()
    $writer.Dispose()
    if ($global:IngestCertBytes) {
        [Array]::Clear($global:IngestCertBytes, 0, $global:IngestCertBytes.Length)
        $global:IngestCertBytes = $null
    }
    $global:IngestAuthConfig = $null
}

return @{
    ChunkIndex = $ChunkNum
    Processed  = $processed
    Skipped    = $skipped
    Errors     = $errors.ToArray()
}
'@

# --- Invoke-EntityPhase2: shared Phase 2 worker abstraction ---

function Invoke-EntityPhase2 {
    param(
        [Parameter(Mandatory)][string]$EntityName,
        [Parameter(Mandatory)][string[]]$EntityIds,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][hashtable]$AuthConfig,
        [byte[]]$CertBytes = $null,
        [int]$PoolSize = 10,
        [Parameter(Mandatory)][ValidateSet('graph', 'exo', 'spo')][string]$ApiFamily,
        [Parameter(Mandatory)][string]$WorkScript,
        [string]$OnSkipScript = $null,
        [int]$FlushInterval = 100,
        [int]$JsonDepth = 5,
        [string]$PreFlightScript = $null
    )

    $moduleName = $script:ModuleNames[$ApiFamily]
    $authScriptStr = $script:AuthScripts[$ApiFamily]
    $reconnectScriptStr = $script:ReconnectScripts[$ApiFamily]

    $pool = New-WorkerPool -ModuleName $moduleName -PoolSize $PoolSize `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -SkipPreAuth -IncludeRetryHelper

    try {
        # --- Pre-authenticate each runspace ---
        $authHandles = @()
        for ($i = 0; $i -lt $PoolSize; $i++) {
            $ps = [PowerShell]::Create()
            $ps.RunspacePool = $pool
            $ps.AddScript($authScriptStr).AddArgument($AuthConfig).AddArgument($CertBytes).AddArgument($i) | Out-Null
            $authHandles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke(); Index = $i }
        }

        $failedRunspaces = 0
        foreach ($item in $authHandles) {
            try {
                $item.PowerShell.EndInvoke($item.Handle) | Out-Null
                if ($item.PowerShell.HadErrors) {
                    foreach ($err in $item.PowerShell.Streams.Error) {
                        Write-Warning "Runspace $($item.Index) auth warning: $($err.Exception.Message)"
                    }
                }
            }
            catch {
                $failedRunspaces++
                Write-Warning "Runspace $($item.Index) auth failed: $($_.Exception.Message)"
            }
            finally {
                $item.PowerShell.Dispose()
            }
        }

        if ($failedRunspaces -eq $PoolSize) {
            throw "All runspaces failed to authenticate. Cannot process $EntityName."
        }

        if ($CertBytes) { [Array]::Clear($CertBytes, 0, $CertBytes.Length) }

        if ($failedRunspaces -gt 0) {
            Write-Warning "$failedRunspaces runspace(s) failed auth. Proceeding with reduced parallelism."
        }

        # --- Dispatch work chunks ---
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript(
                $script:DispatchTemplate
            ).AddArgument(
                $slices[$chunkIndex]      # $ItemIds
            ).AddArgument(
                $OutputDirectory          # $OutputDir
            ).AddArgument(
                $chunkIndex               # $ChunkNum
            ).AddArgument(
                $RunId                    # $RunId
            ).AddArgument(
                $ApiFamily                # $ApiFamily
            ).AddArgument(
                $WorkScript               # $WorkScriptStr
            ).AddArgument(
                $OnSkipScript             # $OnSkipScriptStr
            ).AddArgument(
                $reconnectScriptStr       # $ReconnectScriptStr
            ).AddArgument(
                $FlushInterval            # $FlushInterval
            ).AddArgument(
                $JsonDepth                # $JsonDepth
            ).AddArgument(
                $AuthConfig               # $AuthCfg
            ).AddArgument(
                $PreFlightScript          # $PreFlightScriptStr
            )

            $ps.RunspacePool = $pool
            $handles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke(); ChunkIndex = $chunkIndex }
        }

        # --- Collect results (proven polling pattern) ---
        $completed = [System.Collections.Generic.HashSet[int]]::new()
        $totalProcessed = 0
        $totalSkipped = 0
        $allErrors = [System.Collections.Generic.List[string]]::new()

        while ($completed.Count -lt $handles.Count) {
            foreach ($item in $handles) {
                if ($completed.Contains($item.ChunkIndex)) { continue }
                if ($item.Handle.IsCompleted) {
                    try {
                        $output = $item.PowerShell.EndInvoke($item.Handle)

                        if ($item.PowerShell.HadErrors) {
                            foreach ($err in $item.PowerShell.Streams.Error) {
                                $errEx = $err.Exception
                                $inner = $errEx
                                while ($inner.InnerException) { $inner = $inner.InnerException }
                                $msg = if (-not [string]::IsNullOrWhiteSpace($inner.Message)) { $inner.Message } else { $errEx.Message }
                                $allErrors.Add("chunk=$($item.ChunkIndex): $msg")
                            }
                        }

                        $result = if ($output -and $output.Count -gt 0) { $output[-1] } else { $null }
                        if ($result) {
                            if ($result.Processed) { $totalProcessed += $result.Processed }
                            if ($result.Skipped)   { $totalSkipped += $result.Skipped }
                            if ($result.Errors -and $result.Errors.Count -gt 0) {
                                $allErrors.AddRange([string[]]$result.Errors)
                            }
                        }
                    }
                    catch {
                        $catchEx = $_.Exception
                        $catchInner = $catchEx
                        while ($catchInner.InnerException) { $catchInner = $catchInner.InnerException }
                        $msg = if (-not [string]::IsNullOrWhiteSpace($catchInner.Message)) { $catchInner.Message } else { $catchEx.Message }
                        $allErrors.Add("chunk=$($item.ChunkIndex) fatal: $msg")
                    }
                    finally {
                        $item.PowerShell.Dispose()
                        $completed.Add($item.ChunkIndex) | Out-Null
                    }
                }
            }
            if ($completed.Count -lt $handles.Count) { Start-Sleep -Seconds 5 }
        }

        return @{
            RecordCount  = $totalProcessed
            ChunkCount   = $slices.Count
            SkippedCount = $totalSkipped
            Errors       = $allErrors.ToArray()
        }
    }
    finally {
        $pool.Close()
        $pool.Dispose()
    }
}

Export-ModuleMember -Function New-WorkerPool, Split-WorkItems, Invoke-ParallelWork, Invoke-EntityPhase2
