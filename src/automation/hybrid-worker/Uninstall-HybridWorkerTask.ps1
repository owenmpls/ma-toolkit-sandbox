#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Uninstalls the MA Toolkit Hybrid Worker Scheduled Task.
.PARAMETER RemoveFiles
    If specified, removes all worker files including configuration and logs.
#>
param(
    [switch]$RemoveFiles
)

$taskName = 'MaToolkitHybridWorker'
$installBase = 'C:\ProgramData\MaToolkit\HybridWorker'

# Stop and unregister the scheduled task
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    if ($task.State -eq 'Running') {
        Write-Host "Stopping task '$taskName'..."
        Stop-ScheduledTask -TaskName $taskName
    }
    Write-Host "Removing task '$taskName'..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
else {
    Write-Host "Scheduled task '$taskName' not found."
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

Write-Host "Task '$taskName' uninstalled." -ForegroundColor Green
