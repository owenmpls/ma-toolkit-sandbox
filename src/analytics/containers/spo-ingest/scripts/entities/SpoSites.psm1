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

    # --- Step 2: Enumerate all sites via Microsoft Graph ---
    $headers = @{ Authorization = "Bearer $graphToken" }
    $allSites = @()
    $uri = 'https://graph.microsoft.com/v1.0/sites?search=*&$top=999&$select=id,name,displayName,webUrl,createdDateTime,lastModifiedDateTime,description,siteCollection'

    do {
        $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
        if ($response.value) {
            $allSites += $response.value
        }
        $uri = $response.'@odata.nextLink'
    } while ($uri)

    # --- Step 3: Enrich each site via PnP per-site connection ---
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
