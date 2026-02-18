<#
.SYNOPSIS
    Downloads .NET assembly dependencies for the Hybrid Worker.
.DESCRIPTION
    Fetches NuGet packages required by the Service Bus and Application Insights
    integration. Run this script before packaging the worker for deployment.
    CI/CD runs this script before creating the deployment zip.
#>

#Requires -Version 7.4

param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'dotnet-libs')
)

$ErrorActionPreference = 'Stop'

# Package versions (match cloud-worker Dockerfile)
$packages = @(
    @{ Name = 'Azure.Messaging.ServiceBus';           Version = '7.18.1';  Dll = 'Azure.Messaging.ServiceBus.dll' }
    @{ Name = 'Azure.Core';                            Version = '1.42.0';  Dll = 'Azure.Core.dll' }
    @{ Name = 'Azure.Core.Amqp';                       Version = '1.3.1';   Dll = 'Azure.Core.Amqp.dll' }
    @{ Name = 'Azure.Identity';                        Version = '1.13.1';  Dll = 'Azure.Identity.dll' }
    @{ Name = 'Microsoft.ApplicationInsights';          Version = '2.22.0';  Dll = 'Microsoft.ApplicationInsights.dll' }
    @{ Name = 'System.Memory.Data';                     Version = '1.0.2';   Dll = 'System.Memory.Data.dll' }
    @{ Name = 'System.ClientModel';                     Version = '1.1.0';   Dll = 'System.ClientModel.dll' }
    @{ Name = 'Microsoft.Identity.Client';              Version = '4.66.0';  Dll = 'Microsoft.Identity.Client.dll' }
    @{ Name = 'Microsoft.Bcl.AsyncInterfaces';          Version = '8.0.0';   Dll = 'Microsoft.Bcl.AsyncInterfaces.dll' }
    @{ Name = 'System.Diagnostics.DiagnosticSource';    Version = '8.0.1';   Dll = 'System.Diagnostics.DiagnosticSource.dll' }
)

# Create output directory
if (-not (Test-Path $OutputPath)) {
    New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "hybrid-worker-nuget-$(Get-Random)"
New-Item -Path $tempDir -ItemType Directory -Force | Out-Null

try {
    foreach ($pkg in $packages) {
        $targetDll = Join-Path $OutputPath $pkg.Dll
        if (Test-Path $targetDll) {
            Write-Host "  [SKIP] $($pkg.Name) v$($pkg.Version) — already exists"
            continue
        }

        Write-Host "  [GET]  $($pkg.Name) v$($pkg.Version)..."
        $nupkgUrl = "https://www.nuget.org/api/v2/package/$($pkg.Name)/$($pkg.Version)"
        $nupkgPath = Join-Path $tempDir "$($pkg.Name).$($pkg.Version).nupkg"

        Invoke-WebRequest -Uri $nupkgUrl -OutFile $nupkgPath -ErrorAction Stop

        # Extract and find the DLL (prefer netstandard2.0, then net6.0, then net8.0)
        $extractPath = Join-Path $tempDir "$($pkg.Name)-extracted"
        Expand-Archive -Path $nupkgPath -DestinationPath $extractPath -Force

        $dllFound = $false
        $searchPaths = @(
            "lib/netstandard2.0/$($pkg.Dll)",
            "lib/netstandard2.1/$($pkg.Dll)",
            "lib/net6.0/$($pkg.Dll)",
            "lib/net8.0/$($pkg.Dll)"
        )
        foreach ($searchPath in $searchPaths) {
            $fullPath = Join-Path $extractPath $searchPath
            if (Test-Path $fullPath) {
                Copy-Item -Path $fullPath -Destination $targetDll -Force
                $dllFound = $true
                Write-Host "  [OK]   $($pkg.Dll) (from $searchPath)"
                break
            }
        }

        if (-not $dllFound) {
            Write-Warning "  [WARN] Could not find $($pkg.Dll) in NuGet package $($pkg.Name)"
        }
    }

    Write-Host ''
    Write-Host "Dependencies downloaded to: $OutputPath" -ForegroundColor Green
}
finally {
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
