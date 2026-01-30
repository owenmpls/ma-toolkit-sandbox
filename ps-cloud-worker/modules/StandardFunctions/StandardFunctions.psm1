<#
.SYNOPSIS
    Standard Functions module loader.
.DESCRIPTION
    Dot-sources all function files in the module directory.
#>

$modulePath = $PSScriptRoot

$functionFiles = @(
    'EntraFunctions.ps1',
    'ExchangeFunctions.ps1'
)

foreach ($file in $functionFiles) {
    $filePath = Join-Path $modulePath $file
    if (Test-Path $filePath) {
        . $filePath
    }
    else {
        Write-Warning "Standard function file not found: $filePath"
    }
}
