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

    # --- Enumerate all sites via Microsoft Graph ---
    # Uses Graph Sites.Read.All (avoids Sites.FullControl.All required by Get-PnPTenantSite)
    $allSites = @()
    $uri = 'v1.0/sites?search=*&$top=999&$select=id,name,displayName,webUrl,createdDateTime,lastModifiedDateTime,description,siteCollection'

    do {
        $response = Invoke-PnPGraphMethod -Url $uri -ErrorAction Stop
        $allSites += $response.value
        $uri = $response.'@odata.nextLink'
        if ($uri) {
            # nextLink returns full URL; strip Graph base for Invoke-PnPGraphMethod
            $uri = $uri -replace '^https://graph\.microsoft\.com/', ''
        }
    } while ($uri)

    # --- Enrich each site via PnP per-site connection ---
    foreach ($site in $allSites) {
        $siteUrl = $site.webUrl
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
        }

        try {
            Connect-PnPOnline -Url $siteUrl `
                -ClientId $script:AuthConfig.ClientId `
                -Tenant $script:AuthConfig.TenantDomain `
                -CertificateBase64Encoded $script:AuthConfig.CertificateBase64

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
            # Enrichment failure should not block the site record
            $record.enrichmentError = $_.Exception.Message
        }

        $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add($siteUrl)
        $count++
        if ($count % 100 -eq 0) { $Writer.Flush() }
    }

    # Reconnect to admin URL for clean session state
    Connect-PnPOnline -Url $script:AuthConfig.AdminUrl `
        -ClientId $script:AuthConfig.ClientId `
        -Tenant $script:AuthConfig.TenantDomain `
        -CertificateBase64Encoded $script:AuthConfig.CertificateBase64

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param([string[]]$EntityIds, [string]$OutputDirectory, [string]$RunId, [int]$PoolSize)
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
