function Get-EntityConfig {
    return @{
        Name         = 'teams_channel_tabs'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'graph'
        OutputFile   = 'teams_channel_tabs'
        DetailType   = 'tabs'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # Phase 1: enumerate teams, then collect all channel composite keys
    $filter = "resourceProvisioningOptions/Any(x:x eq 'Team')"
    $uri = "/v1.0/groups?`$filter=$filter&`$select=id&`$top=999"
    $teamIds = [System.Collections.Generic.List[string]]::new()

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($group in $response.value) {
            $teamIds.Add($group.id)
        }
        $uri = $response['@odata.nextLink']
    } while ($uri)

    # For each team, list all channels and collect composite keys
    foreach ($teamId in $teamIds) {
        try {
            $channelUri = "/v1.0/teams/$teamId/channels?`$select=id"
            do {
                $channelResponse = Invoke-MgGraphRequest -Method GET -Uri $channelUri -ErrorAction Stop
                foreach ($channel in $channelResponse.value) {
                    $EntityIds.Add("${teamId}:$($channel.id)")
                }
                $channelUri = $channelResponse['@odata.nextLink']
            } while ($channelUri)
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -notmatch '404|Request_ResourceNotFound') {
                Write-Warning "Failed to list channels for team $teamId $msg"
            }
        }
    }

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

    return Invoke-EntityPhase2 -EntityName 'teams_channel_tabs' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'graph' -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
$parts = $ItemId -split ':', 2
$teamId = $parts[0]; $channelId = $parts[1]
$records = [System.Collections.Generic.List[hashtable]]::new()
$uri = "/v1.0/teams/$teamId/channels/$channelId/tabs?`$expand=teamsApp&`$select=id,displayName,webUrl,configuration"
do {
    $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
    foreach ($tab in $response.value) {
        $record = @{
            teamId         = $teamId
            channelId      = $channelId
            id             = $tab.id
            displayName    = $tab.displayName
            webUrl         = $tab.webUrl
            configuration  = $tab.configuration
            appDisplayName = $tab.teamsApp.displayName
            teamsAppId     = $tab.teamsApp.id
        }
        $records.Add($record)
    }
    $uri = $response['@odata.nextLink']
} while ($uri)
return ,$records.ToArray()
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
