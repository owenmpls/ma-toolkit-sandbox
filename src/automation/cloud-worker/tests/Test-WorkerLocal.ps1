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
    'service-bus.ps1',
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

Test-Step 'ExampleCustomModule manifest exports 4 functions' {
    $manifestPath = Join-Path $modulesPath 'CustomFunctions' 'ExampleCustomModule' 'ExampleCustomModule.psd1'
    $manifest = Import-PowerShellDataFile -Path $manifestPath
    if ($manifest.FunctionsToExport.Count -ne 4) {
        throw "Expected 4 exported functions, got $($manifest.FunctionsToExport.Count)"
    }
}

Test-Step 'ExampleCustomModule exports expected function names' {
    $manifestPath = Join-Path $modulesPath 'CustomFunctions' 'ExampleCustomModule' 'ExampleCustomModule.psd1'
    $manifest = Import-PowerShellDataFile -Path $manifestPath
    $expected = @('Set-ExampleUserAttribute', 'Get-ExampleMailboxInfo', 'Test-ExampleMigrationReady', 'Start-ExampleLongOperation')
    foreach ($fn in $expected) {
        if ($fn -notin $manifest.FunctionsToExport) {
            throw "Missing expected export: $fn"
        }
    }
}

Test-Step 'ExampleCustomModule defines all exported functions' {
    $filePath = Join-Path $modulesPath 'CustomFunctions' 'ExampleCustomModule' 'ExampleCustomModule.psm1'
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$tokens, [ref]$errors)
    $functions = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)
    $functionNames = $functions | ForEach-Object { $_.Name }

    $expected = @('Set-ExampleUserAttribute', 'Get-ExampleMailboxInfo', 'Test-ExampleMigrationReady', 'Start-ExampleLongOperation')
    foreach ($fn in $expected) {
        if ($fn -notin $functionNames) {
            throw "Function '$fn' not defined in psm1"
        }
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

# --- Test 7: Certificate-based auth functions ---
Write-Host ''
Write-Host 'Certificate auth validation:' -ForegroundColor Yellow

Test-Step 'auth.ps1 exports Get-WorkerCertificate' {
    . (Join-Path $srcPath 'auth.ps1')
    $cmd = Get-Command Get-WorkerCertificate -ErrorAction Stop
    if ($cmd.CommandType -ne 'Function') { throw 'Not a function' }
}

Test-Step 'auth.ps1 does not export Get-ExchangeOnlineAccessToken (removed)' {
    . (Join-Path $srcPath 'auth.ps1')
    $cmd = Get-Command Get-ExchangeOnlineAccessToken -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { throw 'Get-ExchangeOnlineAccessToken should have been removed' }
}

Test-Step 'auth.ps1 does not export Get-WorkerAppSecret (removed)' {
    . (Join-Path $srcPath 'auth.ps1')
    $cmd = Get-Command Get-WorkerAppSecret -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { throw 'Get-WorkerAppSecret should have been removed' }
}

Test-Step 'Auth scriptblock does not contain client_secret' {
    . (Join-Path $srcPath 'auth.ps1')
    $scriptBlock = Get-RunspaceAuthScriptBlock -TenantId 'test' -AppId 'test' -Organization 'test.onmicrosoft.com' -CertificateBytes ([byte[]]@(0))
    $scriptText = $scriptBlock.ToString()
    if ($scriptText -match 'client_secret') {
        throw 'Auth scriptblock still contains client_secret reference'
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
