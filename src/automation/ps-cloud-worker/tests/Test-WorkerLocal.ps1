<#
.SYNOPSIS
    Local testing helper for the PowerShell Cloud Worker.
.DESCRIPTION
    Verifies that the worker scripts and modules load correctly
    without requiring live Azure connections. Useful for syntax
    checking and module structure validation.
.EXAMPLE
    ./Test-WorkerLocal.ps1
.EXAMPLE
    ./Test-WorkerLocal.ps1 -Verbose
#>

#Requires -Version 7.4

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$basePath = Split-Path -Parent $PSScriptRoot
$srcPath = Join-Path $basePath 'src'
$modulesPath = Join-Path $basePath 'modules'

$passed = 0
$failed = 0

function Test-Step {
    param(
        [string]$Name,
        [scriptblock]$Test
    )

    try {
        & $Test
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
    catch {
        Write-Host "  [FAIL] $Name : $($_.Exception.Message)" -ForegroundColor Red
        $script:failed++
    }
}

Write-Host ''
Write-Host 'PowerShell Cloud Worker - Local Tests' -ForegroundColor Cyan
Write-Host '======================================' -ForegroundColor Cyan
Write-Host ''

# --- Test 1: Source files parse without errors ---
Write-Host 'Source file parsing:' -ForegroundColor Yellow

$sourceFiles = @(
    'config.ps1',
    'logging.ps1',
    'auth.ps1',
    'throttle-handler.ps1',
    'servicebus.ps1',
    'runspace-manager.ps1',
    'job-dispatcher.ps1'
)

foreach ($file in $sourceFiles) {
    Test-Step "Parse $file" {
        $filePath = Join-Path $srcPath $file
        if (-not (Test-Path $filePath)) {
            throw "File not found: $filePath"
        }
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count -gt 0) {
            throw "Parse errors: $($errors | ForEach-Object { $_.Message } | Select-Object -First 3)"
        }
    }
}

# --- Test 2: Worker entry point parses ---
Write-Host ''
Write-Host 'Worker entry point:' -ForegroundColor Yellow

Test-Step 'Parse worker.ps1' {
    $filePath = Join-Path $srcPath 'worker.ps1'
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        throw "Parse errors: $($errors | ForEach-Object { $_.Message } | Select-Object -First 3)"
    }
}

# --- Test 3: Standard module structure ---
Write-Host ''
Write-Host 'Standard module structure:' -ForegroundColor Yellow

Test-Step 'StandardFunctions.psd1 exists' {
    $path = Join-Path $modulesPath 'StandardFunctions' 'StandardFunctions.psd1'
    if (-not (Test-Path $path)) { throw 'Not found' }
}

Test-Step 'StandardFunctions.psm1 exists' {
    $path = Join-Path $modulesPath 'StandardFunctions' 'StandardFunctions.psm1'
    if (-not (Test-Path $path)) { throw 'Not found' }
}

$moduleFunctionFiles = @('EntraFunctions.ps1', 'ExchangeFunctions.ps1')
foreach ($file in $moduleFunctionFiles) {
    Test-Step "Parse StandardFunctions/$file" {
        $filePath = Join-Path $modulesPath 'StandardFunctions' $file
        if (-not (Test-Path $filePath)) { throw 'Not found' }
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count -gt 0) {
            throw "Parse errors: $($errors | ForEach-Object { $_.Message } | Select-Object -First 3)"
        }
    }
}

# --- Test 4: Standard module manifest is valid ---
Test-Step 'StandardFunctions manifest valid' {
    $manifestPath = Join-Path $modulesPath 'StandardFunctions' 'StandardFunctions.psd1'
    $manifest = Import-PowerShellDataFile -Path $manifestPath
    if (-not $manifest.FunctionsToExport -or $manifest.FunctionsToExport.Count -eq 0) {
        throw 'No functions exported'
    }
    Write-Verbose "Exports $($manifest.FunctionsToExport.Count) functions"
}

# --- Test 5: Custom module structure ---
Write-Host ''
Write-Host 'Custom module structure:' -ForegroundColor Yellow

Test-Step 'ExampleCustomModule.psd1 exists' {
    $path = Join-Path $modulesPath 'CustomFunctions' 'ExampleCustomModule' 'ExampleCustomModule.psd1'
    if (-not (Test-Path $path)) { throw 'Not found' }
}

Test-Step 'ExampleCustomModule.psm1 parses' {
    $filePath = Join-Path $modulesPath 'CustomFunctions' 'ExampleCustomModule' 'ExampleCustomModule.psm1'
    if (-not (Test-Path $filePath)) { throw 'Not found' }
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        throw "Parse errors: $($errors | ForEach-Object { $_.Message } | Select-Object -First 3)"
    }
}

# --- Test 6: Config function loads ---
Write-Host ''
Write-Host 'Functional tests (dot-source):' -ForegroundColor Yellow

Test-Step 'config.ps1 exports Get-WorkerConfiguration' {
    . (Join-Path $srcPath 'config.ps1')
    $cmd = Get-Command Get-WorkerConfiguration -ErrorAction Stop
    if ($cmd.CommandType -ne 'Function') { throw 'Not a function' }
}

Test-Step 'logging.ps1 exports logging functions' {
    . (Join-Path $srcPath 'logging.ps1')
    $expected = @('Initialize-WorkerLogging', 'Write-WorkerLog', 'Write-WorkerException', 'Write-WorkerMetric', 'Write-WorkerEvent', 'Flush-WorkerTelemetry')
    foreach ($fn in $expected) {
        $cmd = Get-Command $fn -ErrorAction Stop
        if ($cmd.CommandType -ne 'Function') { throw "$fn is not a function" }
    }
}

Test-Step 'throttle-handler.ps1 exports throttle functions' {
    . (Join-Path $srcPath 'throttle-handler.ps1')
    $expected = @('Test-IsThrottledException', 'Invoke-WithThrottleRetry')
    foreach ($fn in $expected) {
        $cmd = Get-Command $fn -ErrorAction Stop
        if ($cmd.CommandType -ne 'Function') { throw "$fn is not a function" }
    }
}

# --- Summary ---
Write-Host ''
Write-Host '======================================' -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })
Write-Host ''

if ($failed -gt 0) {
    exit 1
}
