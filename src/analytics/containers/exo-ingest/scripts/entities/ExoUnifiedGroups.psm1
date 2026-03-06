function Get-EntityConfig {
    return @{
        Name         = 'exo_unified_groups'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'exo'
        OutputFile   = 'exo_unified_groups'
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
    Get-UnifiedGroup -ResultSize Unlimited | ForEach-Object {
        $Writer.WriteLine(($_ | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add($_.ExternalDirectoryObjectId)
        $count++
        if ($count % 1000 -eq 0) { $Writer.Flush() }
    }

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param([string[]]$EntityIds, [string]$OutputDirectory, [string]$RunId, [int]$PoolSize)
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
