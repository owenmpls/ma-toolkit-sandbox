function Get-EntityConfig {
    return @{
        Name         = 'entra_group_members'
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
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    $uri = '/v1.0/groups?$select=id,displayName&$top=999'

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
        [Parameter(Mandatory)][string[]]$EntityIds,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][hashtable]$AuthConfig,
        [Parameter(Mandatory)][byte[]]$CertBytes,
        [int]$PoolSize = 10
    )

    return Invoke-EntityPhase2 -EntityName 'entra_group_members' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'graph' -WorkScript @'
param($GroupId, $AuthCfg, $PreFlight)
$records = [System.Collections.Generic.List[hashtable]]::new()
$uri = "/v1.0/groups/$GroupId/members?`$select=id,displayName,userPrincipalName,mail&`$top=999"
do {
    $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
    foreach ($m in $response.value) { $m['groupId'] = $GroupId; $records.Add($m) }
    $uri = $response['@odata.nextLink']
} while ($uri)
return ,$records.ToArray()
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
