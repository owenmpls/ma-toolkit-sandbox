function Get-EntityConfig {
    return @{
        Name         = 'entra_group_members'
        ScheduleTier = 'core_enrichment'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'graph'
        OutputFile   = 'entra_group_members'
        DetailType   = 'members'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    $uri = '/v1.0/groups?$select=id,displayName&$top=999'

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($group in $response.value) {
            if (-not $script:Running) { return }
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
        [int]$PoolSize = 10
    )

    $pool = New-WorkerPool -ModuleName 'Microsoft.Graph.Authentication' `
        -PoolSize $PoolSize `
        -AuthConfig $script:AuthConfig `
        -CertBytes $script:CertBytes

    try {
        # Pre-authenticate each runspace to Graph
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript({
                param($GroupIds, $OutputDir, $ChunkNum, $RunId, $AuthConfig, $CertBytes)

                # Authenticate this runspace
                $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $CertBytes, [string]::Empty,
                    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
                )
                Connect-MgGraph -Certificate $cert -ClientId $AuthConfig.ClientId -TenantId $AuthConfig.TenantId -NoWelcome

                $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
                $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
                $processed = 0

                try {
                    foreach ($groupId in $GroupIds) {
                        $uri = "/v1.0/groups/$groupId/members?`$select=id,displayName,userPrincipalName,mail&`$top=999"
                        do {
                            try {
                                $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
                                foreach ($member in $response.value) {
                                    $member['groupId'] = $groupId
                                    $writer.WriteLine(($member | ConvertTo-Json -Compress -Depth 5))
                                    $processed++
                                }
                                $uri = $response['@odata.nextLink']
                            }
                            catch {
                                if ($_.Exception.Message -match '404|Request_ResourceNotFound') {
                                    $uri = $null  # group deleted, skip
                                } else { throw }
                            }
                        } while ($uri)

                        if ($processed % 100 -eq 0) { $writer.Flush() }
                    }
                }
                finally {
                    $writer.Flush()
                    $writer.Dispose()
                }

                return @{ ChunkIndex = $ChunkNum; Processed = $processed }
            }).AddArgument($slices[$chunkIndex]).AddArgument($OutputDirectory).AddArgument($chunkIndex).AddArgument($RunId).AddArgument($script:AuthConfig).AddArgument($script:CertBytes)

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
