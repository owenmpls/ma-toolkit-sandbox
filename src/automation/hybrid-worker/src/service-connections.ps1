<#
.SYNOPSIS
    Service connection registry for the Hybrid Worker.
.DESCRIPTION
    Scans per-service modules and custom modules, builds a full function catalog
    (FunctionName -> RequiredService), and determines which functions are allowed
    based on enabled services. Supports capability gating: functions for disabled
    services are cataloged but not whitelisted.
#>

function Initialize-ServiceConnections {
    <#
    .SYNOPSIS
        Scans modules and builds function catalog + allowed function whitelist.
    .RETURNS
        Registry object with FunctionCatalog, AllowedFunctions, EnabledServices,
        and EnabledModulePaths.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    $sc = $Config.ServiceConnections

    # Determine which services are enabled
    $enabledServices = @()
    $allServiceNames = @('activeDirectory', 'exchangeServer', 'sharepointOnline', 'teams')
    foreach ($svc in $allServiceNames) {
        if ($sc.$svc.enabled -eq $true) {
            $enabledServices += $svc
        }
    }

    # Scan all service modules (top-level directories under ServiceModulesPath, excluding CustomFunctions)
    $functionCatalog = @{}     # FunctionName -> RequiredService (ALL functions from ALL modules)
    $allowedFunctions = @()    # Only functions for enabled services
    $enabledModulePaths = @()  # Module manifest paths to import in sessions

    $serviceModulesPath = $Config.ServiceModulesPath
    if (Test-Path $serviceModulesPath) {
        $moduleDirs = Get-ChildItem -Path $serviceModulesPath -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'CustomFunctions' }

        foreach ($dir in $moduleDirs) {
            $psd1 = Join-Path $dir.FullName "$($dir.Name).psd1"
            if (-not (Test-Path $psd1)) { continue }

            $manifest = Import-PowerShellDataFile -Path $psd1
            $requiredService = $manifest.PrivateData.RequiredService

            if (-not $requiredService) { continue }

            # Catalog all exported functions
            foreach ($fn in $manifest.FunctionsToExport) {
                $functionCatalog[$fn] = $requiredService
            }

            # If this service is enabled, whitelist the functions and record the module path
            if ($requiredService -in $enabledServices) {
                $allowedFunctions += @($manifest.FunctionsToExport)
                $enabledModulePaths += $psd1
            }
        }
    }

    # Scan custom modules — support both RequiredService (single) and RequiredServices (array)
    if (Test-Path $Config.CustomModulesPath) {
        $customDirs = Get-ChildItem -Path $Config.CustomModulesPath -Directory -ErrorAction SilentlyContinue
        foreach ($dir in $customDirs) {
            $psd1 = Join-Path $dir.FullName "$($dir.Name).psd1"
            if (-not (Test-Path $psd1)) { continue }

            $manifest = Import-PowerShellDataFile -Path $psd1

            # Determine required services (single or array)
            $requiredServices = @()
            if ($manifest.PrivateData.RequiredServices) {
                $requiredServices = @($manifest.PrivateData.RequiredServices)
            }
            elseif ($manifest.PrivateData.RequiredService) {
                $requiredServices = @($manifest.PrivateData.RequiredService)
            }

            if ($requiredServices.Count -eq 0) { continue }

            # For catalog, use the first required service as the representative
            $catalogService = $requiredServices[0]
            foreach ($fn in $manifest.FunctionsToExport) {
                $functionCatalog[$fn] = $catalogService
            }

            # Custom module is enabled only if ALL its required services are enabled
            $allEnabled = $true
            foreach ($rs in $requiredServices) {
                if ($rs -notin $enabledServices) {
                    $allEnabled = $false
                    break
                }
            }

            if ($allEnabled) {
                $allowedFunctions += @($manifest.FunctionsToExport)
                $enabledModulePaths += $psd1
            }
        }
    }

    $registry = [PSCustomObject]@{
        FunctionCatalog    = $functionCatalog
        AllowedFunctions   = $allowedFunctions
        EnabledServices    = $enabledServices
        EnabledModulePaths = $enabledModulePaths
    }

    Write-WorkerLog -Message "Service connections initialized. Enabled: $($enabledServices -join ', ')" -Properties @{
        EnabledServices     = ($enabledServices -join ', ')
        CatalogCount        = $functionCatalog.Count
        AllowedCount        = $allowedFunctions.Count
        EnabledModuleCount  = $enabledModulePaths.Count
    }

    return $registry
}

function Get-AllowedFunctions {
    <#
    .SYNOPSIS
        Returns HashSet of allowed function names from the service registry.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$ServiceRegistry
    )

    $allowed = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($fn in $ServiceRegistry.AllowedFunctions) {
        [void]$allowed.Add($fn)
    }
    return $allowed
}
