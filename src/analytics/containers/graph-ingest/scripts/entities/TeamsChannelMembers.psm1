function Get-EntityConfig {
    return @{
        Name         = 'teams_channel_members'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'graph'
        OutputFile   = 'teams_channel_members'
        DetailType   = 'members'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # Phase 1: enumerate teams, then collect private/shared channel composite keys
    $filter = "resourceProvisioningOptions/Any(x:x eq 'Team')"
    $uri = "/v1.0/groups?`$filter=$filter&`$select=id&`$top=999"
    $teamIds = [System.Collections.Generic.List[string]]::new()

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($group in $response.value) {
            $teamIds.Add($group.id)
        }
        $uri = $response['@odata.nextLink']
    } while ($uri)

    # For each team, list channels and collect private/shared ones
    foreach ($teamId in $teamIds) {
        try {
            $channelUri = "/v1.0/teams/$teamId/channels?`$select=id,membershipType"
            do {
                $channelResponse = Invoke-MgGraphRequest -Method GET -Uri $channelUri -ErrorAction Stop
                foreach ($channel in $channelResponse.value) {
                    if ($channel.membershipType -in @('private', 'shared')) {
                        $EntityIds.Add("${teamId}:$($channel.id)")
                    }
                }
                $channelUri = $channelResponse['@odata.nextLink']
            } while ($channelUri)
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -notmatch '404|Request_ResourceNotFound') {
                Write-Warning "Failed to list channels for team $teamId $msg"
            }
        }
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
        [int]$PoolSize = 5
    )

    $pool = New-WorkerPool -ModuleName 'Microsoft.Graph.Authentication' `
        -PoolSize $PoolSize -AuthConfig $AuthConfig -CertBytes $CertBytes -SkipPreAuth

    try {
        # --- Pre-authenticate each runspace ---
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
            throw "All runspaces failed to authenticate. Cannot enumerate channel members."
        }

        [Array]::Clear($CertBytes, 0, $CertBytes.Length)

        if ($failedRunspaces -gt 0) {
            Write-Warning "$failedRunspaces runspace(s) failed auth. Proceeding with reduced parallelism."
        }

        # --- Dispatch work (composite keys: teamId:channelId) ---
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript({
                param($CompositeKeys, $OutputDir, $ChunkNum, $RunId)

                $MaxRetries = 5
                $BaseDelay = 2
                $MaxDelay = 120

                $authPatterns = @('401', 'Unauthorized', 'token.*expired', 'Access token has expired')
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'ServerBusyException'
                )

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
                    foreach ($compositeKey in $CompositeKeys) {
                        $parts = $compositeKey -split ':', 2
                        $teamId = $parts[0]
                        $channelId = $parts[1]
                        $attempt = 0
                        $itemDone = $false

                        while (-not $itemDone) {
                            $attempt++
                            $uri = "/v1.0/teams/$teamId/channels/$channelId/members?`$select=id,displayName,email,roles"

                            try {
                                do {
                                    $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
                                    foreach ($member in $response.value) {
                                        $member['teamId'] = $teamId
                                        $member['channelId'] = $channelId
                                        $writer.WriteLine(($member | ConvertTo-Json -Compress -Depth 5))
                                        $processed++
                                    }
                                    $uri = $response['@odata.nextLink']
                                } while ($uri)
                                $itemDone = $true
                            }
                            catch {
                                $ex = $_.Exception
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

                                if ($matchText -match '404|Request_ResourceNotFound') {
                                    $skipped++
                                    $itemDone = $true
                                    continue
                                }

                                if ($attempt -ge $MaxRetries) {
                                    $errors.Add("team=$teamId channel=$channelId attempt=${attempt}: $errorMessage")
                                    $skipped++
                                    $itemDone = $true
                                    continue
                                }

                                $isAuthError = $false
                                foreach ($p in $authPatterns) {
                                    if ($matchText -match $p) { $isAuthError = $true; break }
                                }
                                if ($isAuthError) {
                                    try { Reconnect-IngestAuth }
                                    catch { Write-Warning "Reconnect failed (attempt $attempt): $($_.Exception.Message)" }
                                    continue
                                }

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

                                $errors.Add("team=$teamId channel=$channelId attempt=${attempt}: $errorMessage")
                                $skipped++
                                $itemDone = $true
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

        # --- Collect results ---
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
