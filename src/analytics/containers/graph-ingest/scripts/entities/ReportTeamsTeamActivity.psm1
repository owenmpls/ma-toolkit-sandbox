function Get-EntityConfig {
    return @{
        Name         = 'report_teams_team_activity'
        ScheduleTier = 'enrichment'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'report_teams_team_activity'
        DetailType   = $null
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $reportPeriod = $env:REPORT_PERIOD
    if (-not $reportPeriod) { $reportPeriod = 'D180' }

    $uri = "/v1.0/reports/getTeamsTeamActivityDetail(period='$reportPeriod')"

    Invoke-GraphReport -ReportUri $uri `
        -Writer $Writer `
        -RecordCount $RecordCount `
        -EntityIds $EntityIds `
        -EntityIdColumn 'Team Id'
}

function Invoke-Phase2 {
    param(
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
