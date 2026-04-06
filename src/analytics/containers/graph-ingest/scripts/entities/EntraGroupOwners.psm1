function Get-EntityConfig {
    return @{
        Name         = 'entra_group_owners'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'graph'
        OutputFile   = 'entra_group_owners'
        DetailType   = 'owners'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    $uri = '/v1.0/groups?$select=id,displayName&$top=999'

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($group in $response.value) {
            $Writer.WriteLine(($group | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($group.id)
            $count++
        }
        if ($count % 1000 -eq 0 -and $count -gt 0) { $Writer.Flush() }
        $uri = $response['@odata.nextLink']
    } while ($uri)

    $Writer.Flush()
    $RecordCount.Value = $count
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

    $pool = New-WorkerPool -ModuleName 'Microsoft.Graph.Authentication' `
        -PoolSize $PoolSize -AuthConfig $AuthConfig -CertBytes $CertBytes -SkipPreAuth

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
                Connect-MgGraph -ClientId $Config.ClientId -TenantId $Config.TenantId `
                    -Certificate $cert -NoWelcome -ErrorAction Stop
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
            throw "All runspaces failed to authenticate. Cannot enumerate group owners."
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
                param($GroupIds, $OutputDir, $ChunkNum, $RunId)

                $MaxRetries = 5
                $BaseDelay = 2
                $MaxDelay = 120

                # Error patterns aligned with cloud-worker retry logic
                $authPatterns = @('401', 'Unauthorized', 'token.*expired', 'Access token has expired')
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'ServerBusyException'
                )

                # Inline reconnection helper using globals stored during pre-auth
                function Reconnect-IngestAuth {
                    $cfg = $global:IngestAuthConfig
                    $bytes = $global:IngestCertBytes
                    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                        $bytes, [string]::Empty,
                        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
                    )
                    Connect-MgGraph -ClientId $cfg.ClientId -TenantId $cfg.TenantId `
                        -Certificate $cert -NoWelcome -ErrorAction Stop
                }

                $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
                $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
                $processed = 0
                $skipped = 0
                $errors = [System.Collections.Generic.List[string]]::new()

                try {
                    foreach ($groupId in $GroupIds) {
                        $attempt = 0
                        $groupDone = $false

                        while (-not $groupDone) {
                            $attempt++
                            $uri = "/v1.0/groups/$groupId/owners?`$select=id,displayName,userPrincipalName,mail&`$top=999"

                            try {
                                do {
                                    $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
                                    foreach ($owner in $response.value) {
                                        $owner['groupId'] = $groupId
                                        $writer.WriteLine(($owner | ConvertTo-Json -Compress -Depth 5))
                                        $processed++
                                    }
                                    $uri = $response['@odata.nextLink']
                                } while ($uri)
                                $groupDone = $true
                            }
                            catch {
                                $ex = $_.Exception
                                # Walk exception chain to find the deepest meaningful message
                                $innermost = $ex
                                while ($innermost.InnerException) { $innermost = $innermost.InnerException }
                                $errorMessage = if (-not [string]::IsNullOrWhiteSpace($innermost.Message)) {
                                    $innermost.Message
                                } elseif (-not [string]::IsNullOrWhiteSpace($ex.Message)) {
                                    $ex.Message
                                } else {
                                    $ex.GetType().FullName
                                }
                                $matchText = "$($ex.Message) $($innermost.Message)"

                                # Not found — group deleted, skip
                                if ($matchText -match '404|Request_ResourceNotFound') {
                                    $skipped++
                                    $groupDone = $true
                                    continue
                                }

                                # Max retries exhausted
                                if ($attempt -ge $MaxRetries) {
                                    $errors.Add("group=$groupId attempt=${attempt}: $errorMessage")
                                    $skipped++
                                    $groupDone = $true
                                    continue
                                }

                                # Auth error — reconnect and retry
                                $isAuthError = $false
                                foreach ($p in $authPatterns) {
                                    if ($matchText -match $p) { $isAuthError = $true; break }
                                }
                                if ($isAuthError) {
                                    try { Reconnect-IngestAuth }
                                    catch { Write-Warning "Reconnect failed (attempt $attempt): $($_.Exception.Message)" }
                                    continue
                                }

                                # Throttle — exponential backoff with jitter, then retry
                                $isThrottled = $false
                                foreach ($p in $throttlePatterns) {
                                    if ($matchText -match $p) { $isThrottled = $true; break }
                                }
                                if ($isThrottled) {
                                    $retryAfter = 0
                                    if ($matchText -match 'Retry-After[:\s]+(\d+)') {
                                        $retryAfter = [int]$Matches[1]
                                    }
                                    $delay = if ($retryAfter -gt 0) {
                                        $retryAfter
                                    } else {
                                        $exp = [math]::Min($BaseDelay * [math]::Pow(2, $attempt - 1), $MaxDelay)
                                        $jitter = Get-Random -Minimum 0.0 -Maximum ($exp * 0.3)
                                        [math]::Round($exp + $jitter, 1)
                                    }
                                    Start-Sleep -Seconds $delay
                                    continue
                                }

                                # Unrecognized error — record and skip
                                $errors.Add("group=$groupId attempt=${attempt}: $errorMessage")
                                $skipped++
                                $groupDone = $true
                            }
                        }

                        if ($processed % 100 -eq 0) { $writer.Flush() }
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
