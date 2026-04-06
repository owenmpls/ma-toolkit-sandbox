function Get-EntityConfig {
    return @{
        Name         = 'intune_managed_devices'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'intune_managed_devices'
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
    $select = 'id,deviceName,managedDeviceOwnerType,enrolledDateTime,lastSyncDateTime,operatingSystem,complianceState,jailBroken,managementAgent,osVersion,azureADRegistered,deviceEnrollmentType,emailAddress,azureADDeviceId,deviceRegistrationState,isEncrypted,userPrincipalName,model,manufacturer,serialNumber,userId,userDisplayName,totalStorageSpaceInBytes,freeStorageSpaceInBytes,managedDeviceName,partnerReportedThreatState,autopilotEnrolled,isSupervised'
    $uri = "/beta/deviceManagement/managedDevices?`$select=$select&`$top=1000"

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($device in $response.value) {
            $Writer.WriteLine(($device | ConvertTo-Json -Compress -Depth 5))
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
