function Get-EntityConfig {
    return @{
        Name         = 'spo_site_permissions'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'spo'
        OutputFile   = 'spo_site_permissions'
        DetailType   = 'permissions'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # Get Graph access token via PnP connection (established by Connect-ToService.ps1)
    $graphToken = Get-PnPAccessToken -ResourceTypeName Graph
    if (-not $graphToken) {
        throw "Failed to obtain Graph access token from PnP connection"
    }

    $headers = @{ Authorization = "Bearer $graphToken" }

    # Enumerate ALL sites via Graph getAllSites — populates EntityIds for Phase 2
    $uri = 'https://graph.microsoft.com/v1.0/sites/getAllSites?$top=999&$select=id,webUrl'

    do {
        $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
        foreach ($site in $response.value) {
            $EntityIds.Add($site.webUrl)
        }
        $uri = $response.'@odata.nextLink'
    } while ($uri)

    # No file upload in Phase 1 — spo_sites bronze already has the listing
    $RecordCount.Value = 0
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

    return Invoke-EntityPhase2 -EntityName 'spo_site_permissions' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'spo' -JsonDepth 10 -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
$siteUrl = $ItemId

$siteConn = Connect-PnPOnline -Url $siteUrl `
    -ClientId $AuthCfg.ClientId `
    -Tenant $AuthCfg.TenantDomain `
    -CertificateBase64Encoded $AuthCfg.CertificateBase64 `
    -ReturnConnection -ErrorAction Stop

$record = [ordered]@{
    siteUrl = $siteUrl
}

# 1. Sensitivity label (site URL connection)
try {
    $label = Get-PnPSiteSensitivityLabel -Connection $siteConn -ErrorAction Stop
    $record.sensitivityLabel = if ($label) { $label.DisplayName } else { $null }
}
catch {
    $record.sensitivityLabel = $null
}

# 2. Site collection admins
$hasEEEU = $false
$hasGuests = $false
try {
    $admins = Get-PnPSiteCollectionAdmin -Connection $siteConn -ErrorAction Stop
    $record.admins = @($admins | ForEach-Object {
        $isEEEU = [bool]($_.LoginName -match 'spo-grid-all-users')
        $isGuest = [bool]($_.LoginName -match '#ext#|urn:spo:guest')
        if ($isEEEU) { $hasEEEU = $true }
        if ($isGuest) { $hasGuests = $true }
        [ordered]@{
            loginName = $_.LoginName
            title     = $_.Title
            email     = $_.Email
            isEEEU    = $isEEEU
            isGuest   = $isGuest
        }
    })
}
catch {
    $record.admins = @()
    $record.adminsError = $_.Exception.Message
}

# 3. SharePoint groups + members
try {
    $groups = Get-PnPGroup -Connection $siteConn -ErrorAction Stop
    $record.groups = @($groups | ForEach-Object {
        $group = $_
        $members = @()
        try {
            $members = @(Get-PnPGroupMember -Group $group.Title -Connection $siteConn -ErrorAction Stop | ForEach-Object {
                $isEEEU = [bool]($_.LoginName -match 'spo-grid-all-users')
                $isGuest = [bool]($_.LoginName -match '#ext#|urn:spo:guest')
                if ($isEEEU) { $hasEEEU = $true }
                if ($isGuest) { $hasGuests = $true }
                [ordered]@{
                    loginName = $_.LoginName
                    title     = $_.Title
                    email     = $_.Email
                    isEEEU    = $isEEEU
                    isGuest   = $isGuest
                }
            })
        }
        catch { }
        [ordered]@{
            id      = $group.Id
            title   = $group.Title
            owner   = $group.OwnerTitle
            members = $members
        }
    })
}
catch {
    $record.groups = @()
    $record.groupsError = $_.Exception.Message
}

# 4. Role assignments
# Load HasUniqueRoleAssignments and RoleAssignments via
# Get-PnPProperty, then snapshot to array before batch-loading
# sub-properties (avoids concurrent collection modification).
$hasUniqueRoleAssignments = $false
try {
    $web = Get-PnPWeb -Connection $siteConn -ErrorAction Stop
    Get-PnPProperty -ClientObject $web -Property HasUniqueRoleAssignments, RoleAssignments -Connection $siteConn -ErrorAction Stop
    $hasUniqueRoleAssignments = [bool]$web.HasUniqueRoleAssignments
    $record.hasUniqueRoleAssignments = $hasUniqueRoleAssignments

    # Snapshot to array to avoid concurrent modification during Load
    $raList = @($web.RoleAssignments)
    $ctx = Get-PnPContext -Connection $siteConn
    foreach ($ra in $raList) {
        $ctx.Load($ra.Member)
        $ctx.Load($ra.RoleDefinitionBindings)
    }
    $ctx.ExecuteQuery()

    $record.roleAssignments = @($raList | ForEach-Object {
        $ra = $_
        $roleDefs = @($ra.RoleDefinitionBindings | ForEach-Object { $_.Name })
        [ordered]@{
            principalType   = $ra.Member.PrincipalType.ToString()
            principalId     = $ra.Member.Id
            principalName   = $ra.Member.Title
            loginName       = $ra.Member.LoginName
            roleDefinitions = $roleDefs
        }
    })
}
catch {
    $record.hasUniqueRoleAssignments = $false
    $record.roleAssignments = @()
    $record.roleAssignmentsError = $_.Exception.Message
}

# 5. Document libraries (BaseTemplate: 101=DocLib, 700=OneDriveLib, 119=WikiPages)
$docLibs = @()
$listObjects = @()
try {
    $listObjects = @(Get-PnPList -Includes HasUniqueRoleAssignments, RootFolder -Connection $siteConn -ErrorAction Stop | Where-Object {
        $_.BaseTemplate -in @(101, 700, 119)
    })
    $docLibs = @($listObjects | ForEach-Object {
        [ordered]@{
            title                = $_.Title
            id                   = $_.Id.ToString()
            itemCount            = $_.ItemCount
            hasUniquePermissions = [bool]$_.HasUniqueRoleAssignments
            rootFolderUrl        = $_.RootFolder.ServerRelativeUrl
        }
    })
    $record.documentLibraries = @($docLibs | ForEach-Object {
        [ordered]@{
            title                = $_.title
            id                   = $_.id
            itemCount            = $_.itemCount
            hasUniquePermissions = $_.hasUniquePermissions
        }
    })
}
catch {
    $record.documentLibraries = @()
    $record.documentLibrariesError = $_.Exception.Message
}

# 6. Sharing links — use Graph API to enumerate drive items
# and batch-check permissions for link-type entries.
# Steps:
#   a) Get Graph token from PnP connection
#   b) Get site's drives
#   c) For each drive, enumerate ALL items via delta
#   d) Batch-fetch permissions (20/batch), extract link-type perms
$hasOrgWideLinks = $false
$hasAnonymousLinks = $false
try {
    $sharingLinks = @()
    $graphToken = Get-PnPAccessToken -ResourceTypeName Graph -Connection $siteConn -ErrorAction Stop
    $graphHeaders = @{ Authorization = "Bearer $graphToken"; 'Content-Type' = 'application/json' }

    # Parse hostname and site path from siteUrl
    $siteUri = [System.Uri]$siteUrl
    $hostname = $siteUri.Host
    $sitePath = $siteUri.AbsolutePath.TrimEnd('/')

    # Get site ID via Graph
    $graphSiteUrl = if ($sitePath -and $sitePath -ne '/') {
        "https://graph.microsoft.com/v1.0/sites/${hostname}:${sitePath}"
    } else {
        "https://graph.microsoft.com/v1.0/sites/${hostname}:/"
    }
    $graphSite = Invoke-RestMethod -Uri $graphSiteUrl -Headers $graphHeaders -Method Get -ErrorAction Stop
    $siteGraphId = $graphSite.id

    # Get all drives for this site
    $drivesUrl = "https://graph.microsoft.com/v1.0/sites/$siteGraphId/drives?`$select=id,name,driveType"
    $drivesResp = Invoke-RestMethod -Uri $drivesUrl -Headers $graphHeaders -Method Get -ErrorAction Stop
    $drives = @($drivesResp.value)

    $totalItemsAcrossDrives = 0
    $totalPermsChecked = 0
    $permTypesSeen = @{}
    foreach ($drive in $drives) {
        $driveId = $drive.id
        $driveName = $drive.name

        # Enumerate ALL items via delta (no $select — it can filter items)
        $deltaUrl = "https://graph.microsoft.com/v1.0/drives/$driveId/root/delta?`$top=200"
        $allItems = [System.Collections.Generic.List[object]]::new()
        do {
            $deltaResp = Invoke-RestMethod -Uri $deltaUrl -Headers $graphHeaders -Method Get -ErrorAction Stop
            foreach ($item in $deltaResp.value) {
                # Skip root folder and deleted items
                if ($item.id -and ($item.file -or $item.folder) -and -not $item.deleted) {
                    $allItems.Add([ordered]@{
                        id       = $item.id
                        name     = $item.name
                        webUrl   = $item.webUrl
                        itemType = if ($item.folder) { 'folder' } else { 'file' }
                    })
                }
            }
            $deltaUrl = $deltaResp.'@odata.nextLink'
        } while ($deltaUrl)

        # Batch-fetch permissions for ALL items (20 per batch)
        for ($batchStart = 0; $batchStart -lt $allItems.Count; $batchStart += 20) {
            $batchEnd = [Math]::Min($batchStart + 20, $allItems.Count)
            $batchRequests = @()
            for ($bi = $batchStart; $bi -lt $batchEnd; $bi++) {
                $batchRequests += @{
                    id     = "$bi"
                    method = 'GET'
                    url    = "/drives/$driveId/items/$($allItems[$bi].id)/permissions"
                }
            }
            $batchBody = @{ requests = $batchRequests } | ConvertTo-Json -Depth 5
            $batchResp = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/$batch' `
                -Headers $graphHeaders -Method Post -Body $batchBody -ErrorAction Stop

            foreach ($batchItem in $batchResp.responses) {
                if ($batchItem.status -ne 200) { continue }
                $itemIdx = [int]$batchItem.id
                $itemInfo = $allItems[$itemIdx]
                foreach ($perm in $batchItem.body.value) {
                    $totalPermsChecked++
                    # Track all permission types seen
                    foreach ($roleKey in @('link','invitation','inherited','owner')) {
                        if ($perm.$roleKey) { $permTypesSeen[$roleKey] = ($permTypesSeen[$roleKey] ?? 0) + 1 }
                    }
                    if ($perm.roles) { $permTypesSeen["roles:$($perm.roles -join ',')"] = ($permTypesSeen["roles:$($perm.roles -join ',')"] ?? 0) + 1 }
                    if (-not $perm.link) { continue }
                    $link = $perm.link
                    $scope = $link.scope   # 'anonymous', 'organization', 'users'
                    $type = $link.type     # 'view', 'edit', 'embed'
                    if ($scope -eq 'organization') { $hasOrgWideLinks = $true }
                    if ($scope -eq 'anonymous') { $hasAnonymousLinks = $true }
                    $sharingLinks += [ordered]@{
                        linkId             = $perm.id
                        linkUrl            = $link.webUrl
                        itemPath           = $itemInfo.webUrl
                        itemType           = $itemInfo.itemType
                        scope              = $scope
                        type               = $type
                        hasPassword        = [bool]$perm.hasPassword
                        expirationDateTime = $perm.expirationDateTime
                        library            = $driveName
                    }
                }
            }
        }
        $totalItemsAcrossDrives += $allItems.Count
    }
    $record.sharingLinks = $sharingLinks
    $record._sharingLinkDiag = [ordered]@{
        driveCount = $drives.Count
        driveNames = @($drives | ForEach-Object { $_.name })
        totalItems = $totalItemsAcrossDrives
        sampleItems = @($allItems | Select-Object -First 5 | ForEach-Object { "$($_.name) ($($_.itemType))" })
        linksFound = $sharingLinks.Count
        totalPermsChecked = $totalPermsChecked
        permTypes = @($permTypesSeen.Keys)
    }
}
catch {
    $record.sharingLinks = @()
    $record.sharingLinksError = $_.Exception.Message
}

# 7. Site sharing capability — use a separate connection
# to admin URL so it doesn't pollute the site context.
try {
    $adminConn = Connect-PnPOnline -Url $AuthCfg.AdminUrl `
        -ClientId $AuthCfg.ClientId `
        -Tenant $AuthCfg.TenantDomain `
        -CertificateBase64Encoded $AuthCfg.CertificateBase64 `
        -ReturnConnection -ErrorAction Stop
    $tenantSite = Get-PnPTenantSite -Identity $siteUrl -Connection $adminConn -ErrorAction Stop
    $record.sharingCapability = $tenantSite.SharingCapability.ToString()
}
catch {
    $record.sharingCapability = $null
    $record.sharingCapabilityError = $_.Exception.Message
}

# Summary flags
$record.hasEEEU = $hasEEEU
$record.hasGuests = $hasGuests
$record.hasOrgWideLinks = $hasOrgWideLinks
$record.hasAnonymousLinks = $hasAnonymousLinks

return $record
'@ `
        -OnSkipScript @'
param($SiteUrl, $ErrorMessage, $AuthCfg)
return [ordered]@{
    siteUrl                  = $SiteUrl
    sensitivityLabel         = $null
    admins                   = @()
    groups                   = @()
    hasUniqueRoleAssignments = $false
    roleAssignments          = @()
    documentLibraries        = @()
    sharingLinks             = @()
    sharingCapability        = $null
    hasEEEU                  = $false
    hasGuests                = $false
    hasOrgWideLinks          = $false
    hasAnonymousLinks        = $false
    error                    = "SKIPPED: $ErrorMessage"
}
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
