function Get-EntityConfig {
    return @{
        Name         = 'spo_sites'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'spo'
        OutputFile   = 'spo_sites'
        DetailType   = 'usage'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0

    # Get Graph access token via PnP connection (established by Connect-ToService.ps1)
    $graphToken = Get-PnPAccessToken -ResourceTypeName Graph
    if (-not $graphToken) {
        throw "Failed to obtain Graph access token from PnP connection"
    }

    $headers = @{ Authorization = "Bearer $graphToken" }

    # Enumerate ALL sites (team, communication, personal/OneDrive) via Graph getAllSites
    $uri = 'https://graph.microsoft.com/v1.0/sites/getAllSites?$top=999&$select=id,name,displayName,webUrl,createdDateTime,lastModifiedDateTime,description,siteCollection'

    do {
        $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
        foreach ($site in $response.value) {
            $isPersonal = [bool]($site.webUrl -match '-my\.sharepoint\.com/personal/')
            $hostname = $null
            if ($site.siteCollection) { $hostname = $site.siteCollection.hostname }

            $record = [ordered]@{
                id                   = $site.id
                name                 = $site.name
                displayName          = $site.displayName
                webUrl               = $site.webUrl
                description          = $site.description
                createdDateTime      = $site.createdDateTime
                lastModifiedDateTime = $site.lastModifiedDateTime
                hostname             = $hostname
                isPersonalSite       = $isPersonal
            }
            $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($site.webUrl)
            $count++
        }
        if ($count % 1000 -eq 0 -and $count -gt 0) { $Writer.Flush() }
        $uri = $response.'@odata.nextLink'
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

    return Invoke-EntityPhase2 -EntityName 'spo_sites' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'spo' -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
$siteUrl = $ItemId

Connect-PnPOnline -Url $siteUrl `
    -ClientId $AuthCfg.ClientId `
    -Tenant $AuthCfg.TenantDomain `
    -CertificateBase64Encoded $AuthCfg.CertificateBase64 `
    -ErrorAction Stop

$record = [ordered]@{
    siteUrl = $siteUrl
}

$pnpSite = Get-PnPSite -Includes Usage, GroupId, HubSiteId, IsHubSite, IsTeamsConnected, Owner, ReadOnly -ErrorAction Stop
$pnpWeb = Get-PnPWeb -Includes WebTemplate, Configuration, Language -ErrorAction Stop

# Usage
if ($pnpSite.Usage) {
    $record.storageUsed = $pnpSite.Usage.Storage
    $record.storagePercentUsed = $pnpSite.Usage.StoragePercentageUsed
    $record.storageQuota = $pnpSite.Usage.StorageQuota
}

$lists = Get-PnPList -ErrorAction Stop
$totalItems = ($lists | Measure-Object -Property ItemCount -Sum).Sum
$record.totalItemCount = [long]$totalItems
$record.listCount = $lists.Count

# Template & generation
$template = "$($pnpWeb.WebTemplate)#$($pnpWeb.Configuration)"
$record.webTemplate = $template
$modernTemplates = @('GROUP#0', 'SITEPAGEPUBLISHING#0', 'TEAMCHANNEL#0', 'TEAMCHANNEL#1')
$record.isModern = $template -in $modernTemplates

# M365 group association
$gid = $pnpSite.GroupId
$empty = [guid]::Empty
$record.groupId = if ($gid -and $gid -ne $empty) { $gid.ToString() } else { $null }
$record.isGroupConnected = [bool]($gid -and $gid -ne $empty)
$record.isTeamsConnected = [bool]$pnpSite.IsTeamsConnected

# Hub site
$hid = $pnpSite.HubSiteId
$record.hubSiteId = if ($hid -and $hid -ne $empty) { $hid.ToString() } else { $null }
$record.isHubSite = [bool]$pnpSite.IsHubSite

# Owner, lock state, language
$record.owner = if ($pnpSite.Owner) { $pnpSite.Owner.Email } else { $null }
$record.readOnly = [bool]$pnpSite.ReadOnly
$record.language = $pnpWeb.Language

return $record
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
