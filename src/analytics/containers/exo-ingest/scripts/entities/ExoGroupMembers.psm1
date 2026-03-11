function Get-EntityConfig {
    return @{
        Name         = 'exo_group_members'
        ScheduleTier = 'core_enrichment'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'exo'
        OutputFile   = 'exo_group_members'
        DetailType   = 'members'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0

    # Enumerate distribution groups
    Get-DistributionGroup -ResultSize Unlimited | ForEach-Object {
        $record = @{
            Identity                   = $_.Identity
            ExternalDirectoryObjectId  = $_.ExternalDirectoryObjectId
            DisplayName                = $_.DisplayName
            PrimarySmtpAddress         = $_.PrimarySmtpAddress
            GroupType                  = 'DistributionGroup'
        }
        $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add("DG:$($_.Identity)")
        $count++
        if ($count % 1000 -eq 0) { $Writer.Flush() }
    }

    # Enumerate unified groups
    Get-UnifiedGroup -ResultSize Unlimited | ForEach-Object {
        $record = @{
            Identity                   = $_.Identity
            ExternalDirectoryObjectId  = $_.ExternalDirectoryObjectId
            DisplayName                = $_.DisplayName
            PrimarySmtpAddress         = $_.PrimarySmtpAddress
            GroupType                  = 'UnifiedGroup'
        }
        $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add("UG:$($_.Identity)")
        $count++
        if ($count % 1000 -eq 0) { $Writer.Flush() }
    }

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

    $pool = New-WorkerPool -ModuleName 'ExchangeOnlineManagement' `
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
            throw "All runspaces failed to authenticate. Cannot enumerate group members."
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
                param($GroupEntries, $OutputDir, $ChunkNum, $RunId)

                $MaxRetries = 5
                $BaseDelay = 2
                $MaxDelay = 120

                # Error patterns aligned with cloud-worker retry logic
                $authPatterns = @('401', 'Unauthorized', 'token.*expired', 'Access token has expired')
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'ServerBusyException',
                    'MicroDelay', 'BackoffException', 'Too many concurrent'
                )

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
                    foreach ($entry in $GroupEntries) {
                        $parts = $entry -split ':', 2
                        $groupType = $parts[0]
                        $groupIdentity = $parts[1]

                        $attempt = 0
                        $groupDone = $false

                        while (-not $groupDone) {
                            $attempt++

                            try {
                                $members = if ($groupType -eq 'DG') {
                                    Get-DistributionGroupMember -Identity $groupIdentity -ResultSize Unlimited
                                } else {
                                    Get-UnifiedGroupLinks -Identity $groupIdentity -LinkType Members -ResultSize Unlimited
                                }

                                foreach ($member in $members) {
                                    $record = @{
                                        groupIdentity = $groupIdentity
                                        groupType     = $groupType
                                        memberName    = $member.Name
                                        memberType    = $member.RecipientType
                                        primarySmtp   = $member.PrimarySmtpAddress
                                    }
                                    $writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
                                    $processed++
                                }
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
                                if ($matchText -match 'ManagementObjectNotFoundException|couldn''t be found') {
                                    $skipped++
                                    $groupDone = $true
                                    continue
                                }

                                # Max retries exhausted
                                if ($attempt -ge $MaxRetries) {
                                    $errors.Add("group=$groupIdentity attempt=${attempt}: $errorMessage")
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
                                $errors.Add("group=$groupIdentity attempt=${attempt}: $errorMessage")
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
