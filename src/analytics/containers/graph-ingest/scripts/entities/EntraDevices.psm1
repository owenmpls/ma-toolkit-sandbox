function Get-EntityConfig {
    return @{
        Name         = 'entra_devices'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'entra_devices'
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
    $expand = 'registeredOwners($select=id,displayName,userPrincipalName),registeredUsers($select=id,displayName,userPrincipalName)'
    $select = 'id,deviceId,displayName,operatingSystem,operatingSystemVersion,trustType,isManaged,isCompliant,accountEnabled,approximateLastSignInDateTime,createdDateTime,model,manufacturer,profileType,deviceCategory,enrollmentProfileName,onPremisesSyncEnabled,onPremisesLastSyncDateTime,onPremisesSecurityIdentifier,mdmAppId,registrationDateTime'
    $uri = "/beta/devices?`$expand=$expand&`$select=$select&`$top=999"

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($device in $response.value) {
            $Writer.WriteLine(($device | ConvertTo-Json -Compress -Depth 10))
            $EntityIds.Add($device.id)
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
