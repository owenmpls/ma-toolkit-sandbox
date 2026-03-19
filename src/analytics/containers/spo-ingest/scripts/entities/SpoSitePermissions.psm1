function Get-EntityConfig {
    return @{
        Name         = 'spo_site_permissions'
        ScheduleTier = 'enrichment'
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

    $pool = New-WorkerPool -ModuleName 'PnP.PowerShell' `
        -PoolSize $PoolSize -AuthConfig $AuthConfig -CertBytes $CertBytes -SkipPreAuth

    try {
        # --- Pre-authenticate each runspace to admin URL to validate credentials ---
        $authHandles = @()
        for ($i = 0; $i -lt $PoolSize; $i++) {
            $ps = [PowerShell]::Create()
            $ps.RunspacePool = $pool
            $ps.AddScript({
                param($Config, $Idx)
                Connect-PnPOnline -Url $Config.AdminUrl `
                    -ClientId $Config.ClientId `
                    -Tenant $Config.TenantDomain `
                    -CertificateBase64Encoded $Config.CertificateBase64 `
                    -ErrorAction Stop
                $global:IngestAuthConfig = $Config
            }).AddArgument($AuthConfig).AddArgument($i) | Out-Null
            $authHandles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke(); Index = $i }
        }

        $failedRunspaces = 0
        foreach ($item in $authHandles) {
            try {
                $item.PowerShell.EndInvoke($item.Handle) | Out-Null
                if ($item.PowerShell.HadErrors) {
                    foreach ($err in $item.PowerShell.Streams.Error) {
                        Write-Warning "Runspace $($item.Index) auth warning: $($err.Exception.Message)"
                    }
                }
            }
            catch {
                $failedRunspaces++
                Write-Warning "Runspace $($item.Index) auth failed: $($_.Exception.Message)"
            }
            finally {
                $item.PowerShell.Dispose()
            }
        }

        if ($failedRunspaces -eq $PoolSize) {
            throw "All runspaces failed to authenticate. Cannot collect site permissions."
        }

        if ($failedRunspaces -gt 0) {
            Write-Warning "$failedRunspaces runspace(s) failed auth. Proceeding with reduced parallelism."
        }

        # --- Dispatch work with retry, throttle handling, and structured results ---
        $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize
        $handles = @()

        for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
            $ps = [PowerShell]::Create().AddScript({
                param($SiteUrls, $OutputDir, $ChunkNum, $RunId)

                $MaxRetries = 5
                $BaseDelay = 2
                $MaxDelay = 120

                $authPatterns = @('401', 'Unauthorized', 'token.*expired', 'Access token has expired')
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'ServerBusyException'
                )

                $cfg = $global:IngestAuthConfig

                $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
                $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
                $processed = 0
                $skipped = 0
                $errors = [System.Collections.Generic.List[string]]::new()

                try {
                    foreach ($siteUrl in $SiteUrls) {
                        $attempt = 0
                        $siteDone = $false

                        while (-not $siteDone) {
                            $attempt++

                            try {
                                $siteConn = Connect-PnPOnline -Url $siteUrl `
                                    -ClientId $cfg.ClientId `
                                    -Tenant $cfg.TenantDomain `
                                    -CertificateBase64Encoded $cfg.CertificateBase64 `
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

                                # 6. Sharing links — query the hidden "Sharing Links" list.
                                # Every SPO site has this hidden list containing all sharing
                                # link metadata. One paginated query returns everything,
                                # much faster than per-item Get-PnPFileSharingLink calls.
                                $hasOrgWideLinks = $false
                                $hasAnonymousLinks = $false
                                try {
                                    $sharingLinks = @()
                                    $linkItems = Get-PnPListItem -List 'Sharing Links' -PageSize 2000 -Connection $siteConn -ErrorAction Stop
                                    foreach ($li in $linkItems) {
                                        $linkScope = $li.FieldValues['LinkScope']
                                        $linkKind = $li.FieldValues['LinkKind']
                                        $linkUrl = $li.FieldValues['ShareLink']
                                        $itemUrl = $li.FieldValues['ItemUrl']
                                        $expiration = $li.FieldValues['Expiration']
                                        $hasPassword = [bool]$li.FieldValues['HasPassword']

                                        # Map LinkKind int to type string
                                        $typeStr = switch ([int]$linkKind) {
                                            0 { 'direct' }
                                            1 { 'organizationView' }
                                            2 { 'organizationEdit' }
                                            3 { 'anonymousView' }
                                            4 { 'anonymousEdit' }
                                            5 { 'flexibleView' }
                                            6 { 'flexibleEdit' }
                                            default { "kind_$linkKind" }
                                        }

                                        # Map scope
                                        $scopeStr = $null
                                        if ($linkScope) { $scopeStr = $linkScope.ToString() }
                                        if (-not $scopeStr) {
                                            $scopeStr = switch ([int]$linkKind) {
                                                { $_ -in 1,2 } { 'organization' }
                                                { $_ -in 3,4 } { 'anonymous' }
                                                default { $null }
                                            }
                                        }

                                        if ($scopeStr -eq 'organization') { $hasOrgWideLinks = $true }
                                        if ($scopeStr -eq 'anonymous') { $hasAnonymousLinks = $true }

                                        $sharingLinks += [ordered]@{
                                            linkId             = $li.Id.ToString()
                                            linkUrl            = $linkUrl
                                            itemPath           = $itemUrl
                                            itemType           = $null
                                            scope              = $scopeStr
                                            type               = $typeStr
                                            hasPassword        = $hasPassword
                                            expirationDateTime = if ($expiration) { $expiration.ToString('o') } else { $null }
                                            library            = $null
                                        }
                                    }
                                    $record.sharingLinks = $sharingLinks
                                }
                                catch {
                                    $record.sharingLinks = @()
                                    $record.sharingLinksError = $_.Exception.Message
                                }

                                # 7. Site sharing capability — use a separate connection
                                # to admin URL so it doesn't pollute the site context.
                                try {
                                    $adminConn = Connect-PnPOnline -Url $cfg.AdminUrl `
                                        -ClientId $cfg.ClientId `
                                        -Tenant $cfg.TenantDomain `
                                        -CertificateBase64Encoded $cfg.CertificateBase64 `
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

                                $writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 10))
                                $processed++
                                $siteDone = $true
                            }
                            catch {
                                $ex = $_.Exception
                                $innermost = $ex
                                while ($innermost.InnerException) { $innermost = $innermost.InnerException }
                                $errorMessage = if (-not [string]::IsNullOrWhiteSpace($innermost.Message)) {
                                    $innermost.Message
                                } elseif (-not [string]::IsNullOrWhiteSpace($ex.Message)) {
                                    $ex.Message
                                } else {
                                    $ex.GetType().FullName
                                }
                                $matchText = "$($ex.Message) $($innermost.Message)"

                                # Not found / locked / no access — skip
                                if ($matchText -match '404|403|locked|no access|does not exist|Cannot find site') {
                                    $skipped++
                                    $siteDone = $true
                                    continue
                                }

                                # Max retries exhausted
                                if ($attempt -ge $MaxRetries) {
                                    $errors.Add("site=$siteUrl attempt=${attempt}: $errorMessage")
                                    $skipped++
                                    $siteDone = $true
                                    continue
                                }

                                # Auth error — retry (PnP reconnects per-site anyway)
                                $isAuthError = $false
                                foreach ($p in $authPatterns) {
                                    if ($matchText -match $p) { $isAuthError = $true; break }
                                }
                                if ($isAuthError) {
                                    continue
                                }

                                # Throttle — exponential backoff with jitter
                                $isThrottled = $false
                                foreach ($p in $throttlePatterns) {
                                    if ($matchText -match $p) { $isThrottled = $true; break }
                                }
                                if ($isThrottled) {
                                    $retryAfter = 0
                                    if ($matchText -match 'Retry-After[:\s]+(\d+)') {
                                        $retryAfter = [int]$Matches[1]
                                    }
                                    $delay = if ($retryAfter -gt 0) {
                                        $retryAfter
                                    } else {
                                        $exp = [math]::Min($BaseDelay * [math]::Pow(2, $attempt - 1), $MaxDelay)
                                        $jitter = Get-Random -Minimum 0.0 -Maximum ($exp * 0.3)
                                        [math]::Round($exp + $jitter, 1)
                                    }
                                    Start-Sleep -Seconds $delay
                                    continue
                                }

                                # Unrecognized error — record and skip
                                $errors.Add("site=$siteUrl attempt=${attempt}: $errorMessage")
                                $skipped++
                                $siteDone = $true
                            }
                        }

                        if ($processed % 100 -eq 0) { $writer.Flush() }
                    }
                }
                finally {
                    $writer.Flush()
                    $writer.Dispose()
                }

                return @{
                    ChunkIndex = $ChunkNum
                    Processed  = $processed
                    Skipped    = $skipped
                    Errors     = $errors.ToArray()
                }
            }).AddArgument($slices[$chunkIndex]).AddArgument($OutputDirectory).AddArgument($chunkIndex).AddArgument($RunId)

            $ps.RunspacePool = $pool
            $handles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke(); ChunkIndex = $chunkIndex }
        }

        # --- Collect results with exception chain walking ---
        $completed = [System.Collections.Generic.HashSet[int]]::new()
        $totalProcessed = 0
        $totalSkipped = 0
        $allErrors = [System.Collections.Generic.List[string]]::new()

        while ($completed.Count -lt $handles.Count) {
            foreach ($item in $handles) {
                if ($completed.Contains($item.ChunkIndex)) { continue }
                if ($item.Handle.IsCompleted) {
                    try {
                        $output = $item.PowerShell.EndInvoke($item.Handle)

                        if ($item.PowerShell.HadErrors) {
                            foreach ($err in $item.PowerShell.Streams.Error) {
                                $errEx = $err.Exception
                                $inner = $errEx
                                while ($inner.InnerException) { $inner = $inner.InnerException }
                                $msg = if (-not [string]::IsNullOrWhiteSpace($inner.Message)) { $inner.Message } else { $errEx.Message }
                                $allErrors.Add("chunk=$($item.ChunkIndex): $msg")
                            }
                        }

                        $result = if ($output -and $output.Count -gt 0) { $output[-1] } else { $null }
                        if ($result) {
                            if ($result.Processed) { $totalProcessed += $result.Processed }
                            if ($result.Skipped)   { $totalSkipped += $result.Skipped }
                            if ($result.Errors -and $result.Errors.Count -gt 0) {
                                $allErrors.AddRange([string[]]$result.Errors)
                            }
                        }
                    }
                    catch {
                        $catchEx = $_.Exception
                        $catchInner = $catchEx
                        while ($catchInner.InnerException) { $catchInner = $catchInner.InnerException }
                        $msg = if (-not [string]::IsNullOrWhiteSpace($catchInner.Message)) { $catchInner.Message } else { $catchEx.Message }
                        $allErrors.Add("chunk=$($item.ChunkIndex) fatal: $msg")
                    }
                    finally {
                        $item.PowerShell.Dispose()
                        $completed.Add($item.ChunkIndex) | Out-Null
                    }
                }
            }
            if ($completed.Count -lt $handles.Count) { Start-Sleep -Seconds 5 }
        }

        return @{
            RecordCount  = $totalProcessed
            ChunkCount   = $slices.Count
            SkippedCount = $totalSkipped
            Errors       = $allErrors.ToArray()
        }
    }
    finally {
        $pool.Close()
        $pool.Dispose()
    }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
