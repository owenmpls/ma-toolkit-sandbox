<#
.SYNOPSIS
    Local validation tests for the Hybrid Worker.
.DESCRIPTION
    Validates parse correctness, project structure, module manifests, and
    function definitions without requiring Azure credentials or network access.
#>

#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$script:TestsPassed = 0
$script:TestsFailed = 0
$script:TestsRun = 0

$projectRoot = Split-Path -Parent $PSScriptRoot
$srcPath = Join-Path $projectRoot 'src'
$modulesPath = Join-Path $projectRoot 'modules'

function Test-Assert {
    param([string]$Name, [scriptblock]$Test)
    $script:TestsRun++
    try {
        $result = & $Test
        if ($result -eq $true) {
            Write-Host "  [PASS] $Name" -ForegroundColor Green
            $script:TestsPassed++
        }
        else {
            Write-Host "  [FAIL] $Name — returned: $result" -ForegroundColor Red
            $script:TestsFailed++
        }
    }
    catch {
        Write-Host "  [FAIL] $Name — $($_.Exception.Message)" -ForegroundColor Red
        $script:TestsFailed++
    }
}

# ============================================================================
# Section 1: File Structure
# ============================================================================
Write-Host ''
Write-Host '=== File Structure ===' -ForegroundColor Cyan

Test-Assert 'version.txt exists' {
    Test-Path (Join-Path $projectRoot 'version.txt')
}

Test-Assert 'version.txt contains valid semver' {
    $v = (Get-Content (Join-Path $projectRoot 'version.txt') -Raw).Trim()
    $v -match '^\d+\.\d+\.\d+$'
}

Test-Assert '.gitignore exists' {
    Test-Path (Join-Path $projectRoot '.gitignore')
}

Test-Assert '.gitignore excludes dotnet-libs/' {
    $content = Get-Content (Join-Path $projectRoot '.gitignore') -Raw
    $content -match 'dotnet-libs/'
}

Test-Assert 'service-host/HybridWorker.ServiceHost.csproj exists' {
    Test-Path (Join-Path $projectRoot 'service-host/HybridWorker.ServiceHost.csproj')
}

Test-Assert 'service-host/Program.cs exists' {
    Test-Path (Join-Path $projectRoot 'service-host/Program.cs')
}

Test-Assert 'service-host/WorkerProcessService.cs exists' {
    Test-Path (Join-Path $projectRoot 'service-host/WorkerProcessService.cs')
}

Test-Assert 'config/worker-config.example.json exists' {
    Test-Path (Join-Path $projectRoot 'config/worker-config.example.json')
}

Test-Assert 'Install-HybridWorker.ps1 exists' {
    Test-Path (Join-Path $projectRoot 'Install-HybridWorker.ps1')
}

Test-Assert 'Uninstall-HybridWorker.ps1 exists' {
    Test-Path (Join-Path $projectRoot 'Uninstall-HybridWorker.ps1')
}

# ============================================================================
# Section 2: PowerShell File Parse Validation
# ============================================================================
Write-Host ''
Write-Host '=== PowerShell Parse Validation ===' -ForegroundColor Cyan

$srcFiles = @(
    'worker.ps1',
    'config.ps1',
    'logging.ps1',
    'auth.ps1',
    'service-bus.ps1',
    'runspace-manager.ps1',
    'session-pool.ps1',
    'service-connections.ps1',
    'update-manager.ps1',
    'job-dispatcher.ps1',
    'health-check.ps1'
)

foreach ($file in $srcFiles) {
    Test-Assert "src/$file parses without errors" {
        $filePath = Join-Path $srcPath $file
        if (-not (Test-Path $filePath)) { throw "File not found: $filePath" }
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$null, [ref]$errors)
        $errors.Count -eq 0
    }
}

# ============================================================================
# Section 3: src/ directory has exactly 11 .ps1 files
# ============================================================================
Write-Host ''
Write-Host '=== Source File Count ===' -ForegroundColor Cyan

Test-Assert 'src/ contains exactly 11 .ps1 files' {
    $files = Get-ChildItem -Path $srcPath -Filter '*.ps1' -File
    $files.Count -eq 11
}

# ============================================================================
# Section 4: Module Validation
# ============================================================================
Write-Host ''
Write-Host '=== Module Validation ===' -ForegroundColor Cyan

Test-Assert 'StandardFunctions manifest is valid' {
    $psd1 = Join-Path $modulesPath 'StandardFunctions/StandardFunctions.psd1'
    $manifest = Import-PowerShellDataFile -Path $psd1
    $manifest.FunctionsToExport.Count -eq 14
}

Test-Assert 'StandardFunctions.psm1 exists' {
    Test-Path (Join-Path $modulesPath 'StandardFunctions/StandardFunctions.psm1')
}

Test-Assert 'HybridFunctions manifest is valid' {
    $psd1 = Join-Path $modulesPath 'HybridFunctions/HybridFunctions.psd1'
    $manifest = Import-PowerShellDataFile -Path $psd1
    $manifest.FunctionsToExport.Count -ge 9
}

Test-Assert 'HybridFunctions.psm1 exists' {
    Test-Path (Join-Path $modulesPath 'HybridFunctions/HybridFunctions.psm1')
}

Test-Assert 'HybridFunctions ExecutionEngine is SessionPool' {
    $psd1 = Join-Path $modulesPath 'HybridFunctions/HybridFunctions.psd1'
    $manifest = Import-PowerShellDataFile -Path $psd1
    $manifest.PrivateData.ExecutionEngine -eq 'SessionPool'
}

# Parse all module function files
$moduleFunctionFiles = @(
    'StandardFunctions/EntraFunctions.ps1',
    'StandardFunctions/ExchangeFunctions.ps1',
    'HybridFunctions/ADFunctions.ps1',
    'HybridFunctions/ExchangeServerFunctions.ps1',
    'HybridFunctions/SPOFunctions.ps1',
    'HybridFunctions/TeamsFunctions.ps1'
)

foreach ($file in $moduleFunctionFiles) {
    Test-Assert "modules/$file parses without errors" {
        $filePath = Join-Path $modulesPath $file
        if (-not (Test-Path $filePath)) { throw "File not found: $filePath" }
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($filePath, [ref]$null, [ref]$errors)
        $errors.Count -eq 0
    }
}

# ============================================================================
# Section 5: Config Example Validation
# ============================================================================
Write-Host ''
Write-Host '=== Config Validation ===' -ForegroundColor Cyan

Test-Assert 'Example config is valid JSON' {
    $configPath = Join-Path $projectRoot 'config/worker-config.example.json'
    $json = Get-Content $configPath -Raw | ConvertFrom-Json
    $null -ne $json.workerId -and $null -ne $json.serviceBus -and $null -ne $json.auth
}

Test-Assert 'Example config has serviceConnections' {
    $configPath = Join-Path $projectRoot 'config/worker-config.example.json'
    $json = Get-Content $configPath -Raw | ConvertFrom-Json
    $null -ne $json.serviceConnections.activeDirectory -and $null -ne $json.serviceConnections.entra
}

# ============================================================================
# Section 6: Service Host Validation
# ============================================================================
Write-Host ''
Write-Host '=== Service Host ===' -ForegroundColor Cyan

Test-Assert 'csproj targets net8.0' {
    $csproj = Get-Content (Join-Path $projectRoot 'service-host/HybridWorker.ServiceHost.csproj') -Raw
    $csproj -match 'net8.0'
}

Test-Assert 'csproj references WindowsServices package' {
    $csproj = Get-Content (Join-Path $projectRoot 'service-host/HybridWorker.ServiceHost.csproj') -Raw
    $csproj -match 'Microsoft.Extensions.Hosting.WindowsServices'
}

Test-Assert 'Program.cs configures MaToolkitHybridWorker service' {
    $cs = Get-Content (Join-Path $projectRoot 'service-host/Program.cs') -Raw
    $cs -match 'MaToolkitHybridWorker'
}

# ============================================================================
# Results
# ============================================================================
Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host "  Tests: $script:TestsRun total, $script:TestsPassed passed, $script:TestsFailed failed" -ForegroundColor $(if ($script:TestsFailed -eq 0) { 'Green' } else { 'Red' })
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

if ($script:TestsFailed -gt 0) {
    exit 1
}
