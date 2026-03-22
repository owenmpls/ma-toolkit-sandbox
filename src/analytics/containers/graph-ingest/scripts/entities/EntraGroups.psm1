function Get-EntityConfig {
    return @{
        Name         = 'entra_groups'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'entra_groups'
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
    $select = 'id,displayName,description,mail,mailEnabled,mailNickname,securityEnabled,groupTypes,membershipRule,membershipRuleProcessingState,onPremisesSyncEnabled,onPremisesLastSyncDateTime,onPremisesDomainName,onPremisesNetBiosName,onPremisesProvisioningErrors,onPremisesSamAccountName,onPremisesSecurityIdentifier,serviceProvisioningErrors,createdDateTime,proxyAddresses,visibility,resourceProvisioningOptions'
    $uri = "/v1.0/groups?`$select=$select&`$top=999"

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($group in $response.value) {
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
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
