function Get-EntityConfig {
    return @{
        Name         = 'entra_sign_in_logs'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'entra_sign_in_logs'
        DetailType   = $null
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # Read lookback window from tenant config (injected as env var by Invoke-Ingestion)
    $lookbackDays = $env:SIGN_IN_LOOKBACK_DAYS
    if (-not $lookbackDays) { $lookbackDays = 8 }
    $lookbackDate = (Get-Date).AddDays(-[int]$lookbackDays).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

    $count = 0
    $select = 'id,createdDateTime,appDisplayName,appId,ipAddress,clientAppUsed,conditionalAccessStatus,isInteractive,location,resourceDisplayName,resourceId,riskDetail,riskLevelAggregated,riskLevelDuringSignIn,riskState,riskEventTypes_v2,status,userDisplayName,userId,userPrincipalName,deviceDetail'
    $uri = "/v1.0/auditLogs/signIns?`$filter=createdDateTime ge $lookbackDate&`$select=$select&`$top=999&`$orderby=createdDateTime"

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($signIn in $response.value) {
            $Writer.WriteLine(($signIn | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($signIn.id)
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
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
