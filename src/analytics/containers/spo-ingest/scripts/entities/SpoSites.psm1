function Get-EntityConfig {
    return @{
        Name         = 'spo_sites'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'spo'
        OutputFile   = 'spo_sites'
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

    # --- Step 1: Get Graph access token via PnP connection ---
    $graphToken = Get-PnPAccessToken -ResourceTypeName Graph

    if (-not $graphToken) {
        throw "Failed to obtain Graph access token from PnP connection"
    }

    $headers = @{ Authorization = "Bearer $graphToken" }

    # --- Step 2: Enumerate team/communication sites via Microsoft Graph ---
    $allSites = @()
    $siteIds = @{}
    $uri = 'https://graph.microsoft.com/v1.0/sites?search=*&$top=999&$select=id,name,displayName,webUrl,createdDateTime,lastModifiedDateTime,description,siteCollection'

    do {
        $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
        if ($response.value) {
            foreach ($s in $response.value) {
                if ($s.id) { $siteIds[$s.id] = $true }
                $allSites += $s
            }
        }
        $uri = $response.'@odata.nextLink'
    } while ($uri)

    # --- Step 3: Enumerate OneDrive personal sites via user drives ---
    try {
        $users = @()
        $userUri = 'https://graph.microsoft.com/v1.0/users?$select=id,userPrincipalName&$top=999'
        do {
            $response = Invoke-RestMethod -Uri $userUri -Headers $headers -Method Get -ErrorAction Stop
            if ($response.value) { $users += $response.value }
            $userUri = $response.'@odata.nextLink'
        } while ($userUri)

        foreach ($user in $users) {
            try {
                # Get user's OneDrive to discover the personal site URL
                $driveUri = "https://graph.microsoft.com/v1.0/users/$($user.id)/drive?`$select=webUrl"
                $drive = Invoke-RestMethod -Uri $driveUri -Headers $headers -Method Get -ErrorAction Stop

                if (-not $drive.webUrl) { continue }

                # Derive site URL from drive webUrl (strip trailing /Documents path)
                $personalSiteUrl = $drive.webUrl -replace '/[^/]+$', ''
                $parsedUri = [System.Uri]$personalSiteUrl
                $siteHostname = $parsedUri.Host
                $sitePath = $parsedUri.AbsolutePath.TrimEnd('/')

                # Get site object from Graph (same schema as team sites)
                $siteUri = "https://graph.microsoft.com/v1.0/sites/${siteHostname}:${sitePath}?`$select=id,name,displayName,webUrl,createdDateTime,lastModifiedDateTime,description,siteCollection"
                $site = Invoke-RestMethod -Uri $siteUri -Headers $headers -Method Get -ErrorAction Stop

                if ($site.id -and -not $siteIds.ContainsKey($site.id)) {
                    $site | Add-Member -NotePropertyName '_ownerEmail' -NotePropertyValue $user.userPrincipalName -Force
                    $allSites += $site
                    $siteIds[$site.id] = $true
                }
            }
            catch {
                # User doesn't have OneDrive provisioned (404) or inaccessible — skip
            }
        }
    }
    catch {
        Write-Host "WARNING: OneDrive enumeration failed (may lack User.Read.All): $($_.Exception.Message)"
    }

    # --- Step 4: Enrich each site via PnP per-site connection ---
    foreach ($site in $allSites) {
        $siteUrl = $site.webUrl
        if (-not $siteUrl) { continue }

        $isPersonal = [bool]($siteUrl -match '-my\.sharepoint\.com/personal/')
        $hostname = $null
        if ($site.siteCollection) { $hostname = $site.siteCollection.hostname }

        $record = [ordered]@{
            id                   = $site.id
            name                 = $site.name
            displayName          = $site.displayName
            webUrl               = $siteUrl
            description          = $site.description
            createdDateTime      = $site.createdDateTime
            lastModifiedDateTime = $site.lastModifiedDateTime
            hostname             = $hostname
            isPersonalSite       = $isPersonal
            ownerEmail           = $site._ownerEmail
        }

        try {
            Connect-PnPOnline -Url $siteUrl `
                -ClientId $global:AuthConfig.ClientId `
                -Tenant $global:AuthConfig.TenantDomain `
                -CertificateBase64Encoded $global:AuthConfig.CertificateBase64

            $pnpSite = Get-PnPSite -Includes Usage
            if ($pnpSite.Usage) {
                $record.storageUsed = $pnpSite.Usage.Storage
                $record.storagePercentUsed = $pnpSite.Usage.StoragePercentageUsed
            }

            $lists = Get-PnPList
            $totalItems = ($lists | Measure-Object -Property ItemCount -Sum).Sum
            $record.totalItemCount = [long]$totalItems
            $record.listCount = $lists.Count
        }
        catch {
            $record.enrichmentError = $_.Exception.Message
        }

        $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add($siteUrl)
        $count++
        if ($count % 100 -eq 0) { $Writer.Flush() }
    }

    # Reconnect to admin URL for clean session state (only if we have auth config)
    if ($global:AuthConfig.AdminUrl) {
        Connect-PnPOnline -Url $global:AuthConfig.AdminUrl `
            -ClientId $global:AuthConfig.ClientId `
            -Tenant $global:AuthConfig.TenantDomain `
            -CertificateBase64Encoded $global:AuthConfig.CertificateBase64
    }

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param([string[]]$EntityIds, [string]$OutputDirectory, [string]$RunId, [int]$PoolSize)
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
