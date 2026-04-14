function Get-EntityConfig {
    return @{
        Name         = 'teams_channels'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'graph'
        OutputFile   = 'teams_channels'
        DetailType   = 'channels'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # Phase 1 collects team IDs only — no JSONL output
    $filter = "resourceProvisioningOptions/Any(x:x eq 'Team')"
    $uri = "/v1.0/groups?`$filter=$filter&`$select=id&`$top=999"

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($group in $response.value) {
            $EntityIds.Add($group.id)
        }
        $uri = $response['@odata.nextLink']
    } while ($uri)

    $RecordCount.Value = 0
}

function Invoke-Phase2 {
    param(
        [Parameter(Mandatory)][string[]]$EntityIds,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][hashtable]$AuthConfig,
        [Parameter(Mandatory)][byte[]]$CertBytes,
        [int]$PoolSize = 5
    )

    return Invoke-EntityPhase2 -EntityName 'teams_channels' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'graph' -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
$records = [System.Collections.Generic.List[hashtable]]::new()
$uri = "/v1.0/teams/$ItemId/channels?`$select=id,displayName,description,membershipType,createdDateTime,webUrl,email,isArchived"
do {
    $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
    foreach ($channel in $response.value) { $channel['teamId'] = $ItemId; $records.Add($channel) }
    $uri = $response['@odata.nextLink']
} while ($uri)
return ,$records.ToArray()
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
