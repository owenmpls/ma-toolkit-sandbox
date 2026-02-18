#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Uninstalls the MA Toolkit Hybrid Worker Windows Service.
.PARAMETER RemoveFiles
    If specified, removes all worker files including configuration and logs.
#>
param(
    [switch]$RemoveFiles
)

$serviceName = 'MaToolkitHybridWorker'
$installBase = 'C:\ProgramData\MaToolkit\HybridWorker'

# Stop the service if running
$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Write-Host "Stopping service '$serviceName'..."
        Stop-Service $serviceName -Force
        # Wait for the service to fully stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    # Remove the service
    Write-Host "Removing service '$serviceName'..."
    & sc.exe delete $serviceName
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Warning: sc.exe delete returned exit code $LASTEXITCODE" -ForegroundColor Yellow
    }
}
else {
    Write-Host "Service '$serviceName' not found."
}

# Optionally remove installation directory
if ($RemoveFiles) {
    Write-Host "Removing installation directory: $installBase"
    Remove-Item $installBase -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'All worker files removed.'
}
elseif (Test-Path $installBase) {
    $confirm = Read-Host 'Remove all worker files including config and logs? (y/N)'
    if ($confirm -eq 'y') {
        Remove-Item $installBase -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'All worker files removed.'
    }
    else {
        Write-Host "Worker files preserved at: $installBase"
    }
}

Write-Host "Service '$serviceName' uninstalled." -ForegroundColor Green
