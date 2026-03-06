function Get-EntityConfig {
    return @{
        Name         = 'spo_sites'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'spo'
        OutputFile   = 'spo_sites'
        DetailType   = $null
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    $sites = Get-PnPTenantSite -IncludeOneDriveSites -Detailed

    foreach ($site in $sites) {
        $Writer.WriteLine(($site | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add($site.Url)
        $count++
        if ($count % 500 -eq 0) { $Writer.Flush() }
    }

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param([string[]]$EntityIds, [string]$OutputDirectory, [string]$RunId, [int]$PoolSize)
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
