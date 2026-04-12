function Get-EntityConfig {
    return @{
        Name         = 'exo_mailbox_statistics'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'exo'
        OutputFile   = 'exo_mailbox_statistics'
        DetailType   = 'statistics'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # Lightweight enumeration — collect ExchangeGuid values only.
    # No records written (Phase 1 upload is skipped when $RecordCount -eq 0).
    Get-EXOMailbox -PropertySets StatisticsSeed -ResultSize Unlimited | ForEach-Object {
        $EntityIds.Add($_.ExchangeGuid.ToString())
    }

    $RecordCount.Value = 0
}

function Invoke-Phase2 {
    param(
        [Parameter(Mandatory)][string[]]$EntityIds,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][hashtable]$AuthConfig,
        [Parameter(Mandatory)][byte[]]$CertBytes,
        [int]$PoolSize = 10
    )

    $pool = New-WorkerPool -ModuleName 'ExchangeOnlineManagement' `
        -PoolSize $PoolSize -AuthConfig $AuthConfig -CertBytes $CertBytes -SkipPreAuth -IncludeRetryHelper

    try {
        # --- Pre-authenticate each runspace with graceful degradation ---
        $authHandles = @()
        for ($i = 0; $i -lt $PoolSize; $i++) {
            $ps = [PowerShell]::Create()
            $ps.RunspacePool = $pool
            $ps.AddScript({
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
                # Store for reactive reconnection (clone bytes so main context can zero its copy)
                $global:IngestAuthConfig = $Config
                $global:IngestCertBytes = [byte[]]$Bytes.Clone()
            }).AddArgument($AuthConfig).AddArgument($CertBytes).AddArgument($i) | Out-Null
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
            throw "All runspaces failed to authenticate. Cannot collect mailbox statistics."
        }

        # Zero certificate bytes in main context (runspaces hold their own clones)
        [Array]::Clear($CertBytes, 0, $CertBytes.Length)

        if ($failedRunspaces -gt 0) {
            Write-Warning "$failedRunspaces runspace(s) failed auth. Proceeding with reduced parallelism."
        }

        # --- Dispatch work with retry, reconnect, and structured results ---
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript({
                param($Guids, $OutputDir, $ChunkNum, $RunId)

                $MaxRetries = 5

                # Inline reconnection helper using globals stored during pre-auth
                function Reconnect-IngestAuth {
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
                }

                $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
                $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
                $processed = 0
                $skipped = 0
                $errors = [System.Collections.Generic.List[string]]::new()

                try {
                    foreach ($guid in $Guids) {
                        $attempt = 0
                        $mbxDone = $false

                        while (-not $mbxDone) {
                            $attempt++

                            try {
                                $stats = Get-EXOMailboxStatistics -Identity $guid -PropertySets All -ErrorAction Stop
                                # Dump entire object — ByteQuantifiedSize fields (TotalItemSize,
                                # TotalDeletedItemSize) serialize as structs, but TablesTotalSize
                                # is a plain bigint that the silver layer uses instead.
                                $writer.WriteLine(($stats | ConvertTo-Json -Compress -Depth 5))
                                $processed++
                                $mbxDone = $true
                            }
                            catch {
                                $class = Get-ErrorClassification -ErrorRecord $_ -ApiFamily 'exo'

                                # Not found / skippable — skip this entity
                                if ($class.Category -eq 'Skippable') {
                                    $skipped++
                                    $mbxDone = $true
                                    continue
                                }

                                # Max retries exhausted
                                if ($attempt -ge $MaxRetries) {
                                    $errors.Add("mbx=$guid attempt=${attempt}: $($class.Message)")
                                    $skipped++
                                    $mbxDone = $true
                                    continue
                                }

                                # Auth error — reconnect and retry
                                if ($class.Category -eq 'Auth') {
                                    try { Reconnect-IngestAuth }
                                    catch { Write-Warning "Reconnect failed (attempt $attempt): $($_.Exception.Message)" }
                                    continue
                                }

                                # Throttle — backoff and retry
                                if ($class.Category -eq 'Throttle') {
                                    Start-Sleep -Seconds (Get-RetryDelay -Classification $class -Attempt $attempt)
                                    continue
                                }

                                # Unrecognized error — record and skip
                                $errors.Add("mbx=$guid attempt=${attempt}: $($class.Message)")
                                $skipped++
                                $mbxDone = $true
                            }
                        }

                        if ($processed % 50 -eq 0) { $writer.Flush() }
                    }
                }
                finally {
                    $writer.Flush()
                    $writer.Dispose()
                }

                return @{
                    ChunkIndex = $ChunkNum
                    Processed  = $processed
                    Skipped    = $skipped
                    Errors     = $errors.ToArray()
                }
            }).AddArgument($slices[$chunkIndex]).AddArgument($OutputDirectory).AddArgument($chunkIndex).AddArgument($RunId)

            $ps.RunspacePool = $pool
            $handles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke(); ChunkIndex = $chunkIndex }
        }

        # --- Collect results with exception chain walking ---
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

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
