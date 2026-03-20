function Get-EntityConfig {
    return @{
        Name         = 'spo_sites'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'spo'
        OutputFile   = 'spo_sites'
        DetailType   = 'usage'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0

    # Get Graph access token via PnP connection (established by Connect-ToService.ps1)
    $graphToken = Get-PnPAccessToken -ResourceTypeName Graph
    if (-not $graphToken) {
        throw "Failed to obtain Graph access token from PnP connection"
    }

    $headers = @{ Authorization = "Bearer $graphToken" }

    # Enumerate ALL sites (team, communication, personal/OneDrive) via Graph getAllSites
    $uri = 'https://graph.microsoft.com/v1.0/sites/getAllSites?$top=999&$select=id,name,displayName,webUrl,createdDateTime,lastModifiedDateTime,description,siteCollection'

    do {
        $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
        foreach ($site in $response.value) {
            $isPersonal = [bool]($site.webUrl -match '-my\.sharepoint\.com/personal/')
            $hostname = $null
            if ($site.siteCollection) { $hostname = $site.siteCollection.hostname }

            $record = [ordered]@{
                id                   = $site.id
                name                 = $site.name
                displayName          = $site.displayName
                webUrl               = $site.webUrl
                description          = $site.description
                createdDateTime      = $site.createdDateTime
                lastModifiedDateTime = $site.lastModifiedDateTime
                hostname             = $hostname
                isPersonalSite       = $isPersonal
            }
            $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($site.webUrl)
            $count++
        }
        if ($count % 1000 -eq 0 -and $count -gt 0) { $Writer.Flush() }
        $uri = $response.'@odata.nextLink'
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

    $pool = New-WorkerPool -ModuleName 'PnP.PowerShell' `
        -PoolSize $PoolSize -AuthConfig $AuthConfig -CertBytes $CertBytes -SkipPreAuth

    try {
        # --- Pre-authenticate each runspace to admin URL to validate credentials ---
        $authHandles = @()
        for ($i = 0; $i -lt $PoolSize; $i++) {
            $ps = [PowerShell]::Create()
            $ps.RunspacePool = $pool
            $ps.AddScript({
                param($Config, $Idx)
                # Store config first so work scriptblocks can access it even if pre-auth fails
                $global:IngestAuthConfig = $Config
                Connect-PnPOnline -Url $Config.AdminUrl `
                    -ClientId $Config.ClientId `
                    -Tenant $Config.TenantDomain `
                    -CertificateBase64Encoded $Config.CertificateBase64 `
                    -ErrorAction Stop
            }).AddArgument($AuthConfig).AddArgument($i) | Out-Null
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
            throw "All runspaces failed to authenticate. Cannot enrich sites."
        }

        if ($failedRunspaces -gt 0) {
            Write-Warning "$failedRunspaces runspace(s) failed auth. Proceeding with reduced parallelism."
        }

        # --- Dispatch work with retry, throttle handling, and structured results ---
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript({
                param($SiteUrls, $OutputDir, $ChunkNum, $RunId)

                $MaxRetries = 5
                $BaseDelay = 2
                $MaxDelay = 120

                $authPatterns = @('401', 'Unauthorized', 'token.*expired', 'Access token has expired')
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'ServerBusyException'
                )

                $cfg = $global:IngestAuthConfig

                $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
                $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
                $processed = 0
                $skipped = 0
                $errors = [System.Collections.Generic.List[string]]::new()

                try {
                    foreach ($siteUrl in $SiteUrls) {
                        $attempt = 0
                        $siteDone = $false

                        while (-not $siteDone) {
                            $attempt++

                            try {
                                Connect-PnPOnline -Url $siteUrl `
                                    -ClientId $cfg.ClientId `
                                    -Tenant $cfg.TenantDomain `
                                    -CertificateBase64Encoded $cfg.CertificateBase64 `
                                    -ErrorAction Stop

                                $record = [ordered]@{
                                    siteUrl = $siteUrl
                                }

                                $pnpSite = Get-PnPSite -Includes Usage -ErrorAction Stop
                                if ($pnpSite.Usage) {
                                    $record.storageUsed = $pnpSite.Usage.Storage
                                    $record.storagePercentUsed = $pnpSite.Usage.StoragePercentageUsed
                                }

                                $lists = Get-PnPList -ErrorAction Stop
                                $totalItems = ($lists | Measure-Object -Property ItemCount -Sum).Sum
                                $record.totalItemCount = [long]$totalItems
                                $record.listCount = $lists.Count

                                $writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
                                $processed++
                                $siteDone = $true
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

                                # Not found / locked / no access — skip
                                if ($matchText -match '404|403|locked|no access|does not exist|Cannot find site') {
                                    $skipped++
                                    $siteDone = $true
                                    continue
                                }

                                # Max retries exhausted
                                if ($attempt -ge $MaxRetries) {
                                    $errors.Add("site=$siteUrl attempt=${attempt}: $errorMessage")
                                    $skipped++
                                    $siteDone = $true
                                    continue
                                }

                                # Auth error — retry (PnP reconnects per-site anyway)
                                $isAuthError = $false
                                foreach ($p in $authPatterns) {
                                    if ($matchText -match $p) { $isAuthError = $true; break }
                                }
                                if ($isAuthError) {
                                    continue
                                }

                                # Throttle — exponential backoff with jitter
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
                                $errors.Add("site=$siteUrl attempt=${attempt}: $errorMessage")
                                $skipped++
                                $siteDone = $true
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
