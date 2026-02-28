function New-WorkerPool {
    param(
        [Parameter(Mandatory)][string]$ModuleName,
        [Parameter(Mandatory)][int]$PoolSize,
        [Parameter(Mandatory)][hashtable]$AuthConfig,
        [byte[]]$CertBytes = $null,
        [switch]$SkipPreAuth
    )

    $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    $iss.ImportPSModule($ModuleName)

    $pool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(
        1, $PoolSize, $iss, (Get-Host)
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

Export-ModuleMember -Function New-WorkerPool, Split-WorkItems, Invoke-ParallelWork
