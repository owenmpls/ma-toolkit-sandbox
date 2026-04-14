function Get-EntityConfig {
    return @{
        Name         = 'teams_installed_apps'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'graph'
        OutputFile   = 'teams_installed_apps'
        DetailType   = 'apps'
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

    return Invoke-EntityPhase2 -EntityName 'teams_installed_apps' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'graph' -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
$records = [System.Collections.Generic.List[hashtable]]::new()
$uri = "/v1.0/teams/$ItemId/installedApps?`$expand=teamsAppDefinition"
do {
    $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
    foreach ($app in $response.value) {
        $record = @{
            teamId          = $ItemId
            appId           = $app.id
            displayName     = $app.teamsAppDefinition.displayName
            teamsAppId      = $app.teamsAppDefinition.teamsAppId
            version         = $app.teamsAppDefinition.version
            publishingState = $app.teamsAppDefinition.publishingState
        }
        $records.Add($record)
    }
    $uri = $response['@odata.nextLink']
} while ($uri)
return ,$records.ToArray()
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
