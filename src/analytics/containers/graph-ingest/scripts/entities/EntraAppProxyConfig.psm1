function Get-EntityConfig {
    return @{
        Name         = 'entra_app_proxy_config'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'graph'
        OutputFile   = 'entra_app_proxy_config'
        DetailType   = 'proxy_config'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    $uri = '/v1.0/applications?$select=id&$top=999'

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($app in $response.value) {
            $Writer.WriteLine(($app | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($app.id)
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

    return Invoke-EntityPhase2 -EntityName 'entra_app_proxy_config' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'graph' -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
# Uses beta endpoint - onPremisesPublishing is not available in v1.0
$response = Invoke-MgGraphRequest -Method GET -Uri "/beta/applications/$ItemId?`$select=id,displayName,onPremisesPublishing" -ErrorAction Stop
# Only return a record if App Proxy is configured
if ($response.onPremisesPublishing) {
    $record = @{
        applicationId        = $ItemId
        id                   = $response.id
        displayName          = $response.displayName
        onPremisesPublishing = $response.onPremisesPublishing
    }
    return $record
}
return $null
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
