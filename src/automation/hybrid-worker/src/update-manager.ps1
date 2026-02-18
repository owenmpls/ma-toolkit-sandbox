<#
.SYNOPSIS
    Self-update manager for the Hybrid Worker.
.DESCRIPTION
    Checks Azure Blob Storage for newer versions, downloads and stages updates.
    The actual version swap happens at startup via Apply-PendingUpdate.
#>

function Test-UpdateAvailable {
    <#
    .SYNOPSIS
        Checks blob storage for a newer version.
    .RETURNS
        Update info object if newer version available, $null otherwise.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    try {
        # Read current version
        $versionFile = Join-Path $Config.InstallPath 'current\version.txt'
        $currentVersion = if (Test-Path $versionFile) {
            (Get-Content $versionFile -Raw).Trim()
        } else { '0.0.0' }

        # Download version.json from blob storage
        $context = New-AzStorageContext -StorageAccountName $Config.UpdateStorageAccount -ErrorAction Stop
        $tempFile = [System.IO.Path]::GetTempFileName()

        Get-AzStorageBlobContent -Container $Config.UpdateContainerName `
            -Blob 'version.json' -Destination $tempFile `
            -Context $context -Force -ErrorAction Stop | Out-Null

        $manifest = Get-Content $tempFile -Raw | ConvertFrom-Json
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue

        # Compare versions
        $current = [Version]$currentVersion
        $available = [Version]$manifest.version

        if ($available -gt $current) {
            Write-WorkerLog -Message "Update available: $currentVersion -> $($manifest.version)" -Properties @{
                CurrentVersion   = $currentVersion
                AvailableVersion = $manifest.version
            }
            return $manifest
        }

        return $null
    }
    catch {
        Write-WorkerLog -Message "Update check failed: $($_.Exception.Message)" -Severity Warning
        return $null
    }
}

function Install-WorkerUpdate {
    <#
    .SYNOPSIS
        Downloads and stages an update package.
    .DESCRIPTION
        Downloads the zip, verifies SHA256, extracts to staging directory,
        writes an update marker file. The actual swap happens at next startup
        via Apply-PendingUpdate.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config,

        [Parameter(Mandatory)]
        [PSCustomObject]$UpdateInfo
    )

    try {
        $stagingPath = Join-Path $Config.InstallPath 'staging'
        $zipPath = Join-Path $Config.InstallPath "staging-$($UpdateInfo.version).zip"

        # Clean staging directory
        if (Test-Path $stagingPath) { Remove-Item $stagingPath -Recurse -Force }
        New-Item -Path $stagingPath -ItemType Directory -Force | Out-Null

        # Download zip from blob storage
        $context = New-AzStorageContext -StorageAccountName $Config.UpdateStorageAccount -ErrorAction Stop
        Get-AzStorageBlobContent -Container $Config.UpdateContainerName `
            -Blob $UpdateInfo.fileName -Destination $zipPath `
            -Context $context -Force -ErrorAction Stop | Out-Null

        # Verify SHA256
        $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
        if ($hash -ne $UpdateInfo.sha256) {
            Remove-Item $zipPath -Force
            throw "SHA256 mismatch: expected $($UpdateInfo.sha256), got $hash"
        }

        # Extract
        Expand-Archive -Path $zipPath -DestinationPath $stagingPath -Force
        Remove-Item $zipPath -Force

        # Write version.txt into staging
        $UpdateInfo.version | Set-Content -Path (Join-Path $stagingPath 'version.txt') -NoNewline

        # Write update marker
        @{
            version     = $UpdateInfo.version
            downloadedAt = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json | Set-Content -Path (Join-Path $Config.InstallPath 'update-pending.json')

        Write-WorkerLog -Message "Update v$($UpdateInfo.version) staged successfully."
        Write-WorkerEvent -EventName 'UpdateStaged' -Properties @{ Version = $UpdateInfo.version }
        return $true
    }
    catch {
        Write-WorkerLog -Message "Update download/staging failed: $($_.Exception.Message)" -Severity Error
        Write-WorkerException -Exception $_.Exception
        return $false
    }
}

function Apply-PendingUpdate {
    <#
    .SYNOPSIS
        Called at startup. If an update marker exists, swaps current -> previous, staging -> current.
    #>
    [CmdletBinding()]
    param(
        [string]$InstallPath = 'C:\ProgramData\MaToolkit\HybridWorker'
    )

    $markerFile = Join-Path $InstallPath 'update-pending.json'
    if (-not (Test-Path $markerFile)) { return $false }

    $marker = Get-Content $markerFile -Raw | ConvertFrom-Json
    Write-Host "[UPDATE] Applying pending update to v$($marker.version)..."

    $currentPath = Join-Path $InstallPath 'current'
    $stagingPath = Join-Path $InstallPath 'staging'
    $previousPath = Join-Path $InstallPath 'previous'

    try {
        # Remove old previous (only keep one rollback version)
        if (Test-Path $previousPath) { Remove-Item $previousPath -Recurse -Force }

        # current -> previous
        if (Test-Path $currentPath) { Rename-Item $currentPath -NewName 'previous' }

        # staging -> current
        Rename-Item $stagingPath -NewName 'current'

        # Clear marker
        Remove-Item $markerFile -Force

        Write-Host "[UPDATE] Successfully updated to v$($marker.version)."
        return $true
    }
    catch {
        Write-Host "[UPDATE] FAILED to apply update: $($_.Exception.Message)" -ForegroundColor Red
        # Attempt rollback: previous -> current
        try {
            if (-not (Test-Path $currentPath) -and (Test-Path $previousPath)) {
                Rename-Item $previousPath -NewName 'current'
                Write-Host '[UPDATE] Rolled back to previous version.' -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "[UPDATE] ROLLBACK FAILED: $($_.Exception.Message)" -ForegroundColor Red
        }
        return $false
    }
}
