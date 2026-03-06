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
        if (-not $script:Running) { return }
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
        if (-not $script:Running) { return }
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
        [int]$PoolSize = 10
    )

    $pool = New-WorkerPool -ModuleName 'ExchangeOnlineManagement' `
        -PoolSize $PoolSize `
        -AuthConfig $script:AuthConfig `
        -CertBytes $script:CertBytes

    try {
        # Pre-authenticate each runspace
        $authSlices = Split-WorkItems -Items @('auth') -SliceCount 1
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
                    Certificate = $cert
                    AppId       = $Config.ClientId
                    Organization = $Config.Organization
                    ShowBanner  = $false
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

        # Dispatch work
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript({
                param($GroupEntries, $OutputDir, $ChunkNum, $RunId)

                $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
                $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
                $processed = 0

                try {
                    foreach ($entry in $GroupEntries) {
                        $parts = $entry -split ':', 2
                        $groupType = $parts[0]
                        $groupIdentity = $parts[1]

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
                        }
                        catch {
                            if ($_.Exception.Message -match 'ManagementObjectNotFoundException|couldn''t be found') {
                                continue  # group deleted, skip
                            }
                            # Throttle retry with backoff
                            if ($_.Exception.Message -match '429|TooManyRequests|ServerBusy|throttl') {
                                Start-Sleep -Seconds (Get-Random -Minimum 10 -Maximum 30)
                                continue
                            }
                            throw
                        }

                        if ($processed % 100 -eq 0) { $writer.Flush() }
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
