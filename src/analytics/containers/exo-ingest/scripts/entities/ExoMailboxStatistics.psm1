function Get-EntityConfig {
    return @{
        Name         = 'exo_mailbox_statistics'
        ScheduleTier = 'enrichment'
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
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$EntityIds
    )

    # Lightweight enumeration — collect ExchangeGuid values only.
    # No records written (Phase 1 upload is skipped when $RecordCount -eq 0).
    Get-EXOMailbox -PropertySets StatisticsSeed -ResultSize Unlimited | ForEach-Object {
        if (-not $script:Running) { return }
        $EntityIds.Add($_.ExchangeGuid.ToString())
    }

    $RecordCount.Value = 0
}

function Invoke-Phase2 {
    param(
        [Parameter(Mandatory)][string[]]$EntityIds,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [int]$PoolSize = 10
    )

    $pool = New-WorkerPool -ModuleName 'ExchangeOnlineManagement' `
        -PoolSize $PoolSize `
        -AuthConfig $script:AuthConfig `
        -CertBytes $script:CertBytes

    try {
        # Pre-authenticate each runspace to EXO
        $authHandles = @()
        for ($i = 0; $i -lt $PoolSize; $i++) {
            $ps = [PowerShell]::Create().AddScript({
                param($Config, $CertBytes)
                $global:ExportAuthConfig = $Config
                $global:ExportCertBytes = $CertBytes
                $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $CertBytes, [string]::Empty,
                    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
                )
                $params = @{
                    Certificate  = $cert
                    AppId        = $Config.ClientId
                    Organization = $Config.Organization
                    ShowBanner   = $false
                }
                Connect-ExchangeOnline @params
            }).AddArgument($script:AuthConfig).AddArgument($script:CertBytes)
            $ps.RunspacePool = $pool
            $authHandles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke() }
        }
        foreach ($item in $authHandles) {
            $item.PowerShell.EndInvoke($item.Handle)
            $item.PowerShell.Dispose()
        }

        # Dispatch parallel work — one Get-EXOMailboxStatistics call per ExchangeGuid
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript({
                param($Guids, $OutputDir, $ChunkNum, $RunId)

                $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
                $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
                $processed = 0

                try {
                    foreach ($guid in $Guids) {
                        try {
                            $stats = Get-EXOMailboxStatistics -Identity $guid -PropertySets All
                            $writer.WriteLine(($stats | ConvertTo-Json -Compress -Depth 5))
                            $processed++
                        }
                        catch {
                            if ($_.Exception.Message -match 'MapiExceptionNotFound|couldn''t be found|mailbox.*doesn''t exist') {
                                continue  # deleted/inactive mailbox, skip
                            }
                            if ($_.Exception.Message -match '429|TooManyRequests|ServerBusy|throttl') {
                                Start-Sleep -Seconds (Get-Random -Minimum 10 -Maximum 30)
                                # Retry once after backoff
                                try {
                                    $stats = Get-EXOMailboxStatistics -Identity $guid -PropertySets All
                                    $writer.WriteLine(($stats | ConvertTo-Json -Compress -Depth 5))
                                    $processed++
                                }
                                catch {
                                    continue  # skip on second failure
                                }
                                continue
                            }
                            throw
                        }

                        if ($processed % 50 -eq 0) { $writer.Flush() }
                    }
                }
                finally {
                    $writer.Flush()
                    $writer.Dispose()
                }

                return @{ ChunkIndex = $ChunkNum; Processed = $processed }
            }).AddArgument($slices[$chunkIndex]).AddArgument($OutputDirectory).AddArgument($chunkIndex).AddArgument($RunId)

            $ps.RunspacePool = $pool
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
    finally {
        $pool.Close()
        $pool.Dispose()
    }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
