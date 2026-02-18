<#
.SYNOPSIS
    Service connection registry for the Hybrid Worker.
.DESCRIPTION
    Maps function names to execution engines based on which services are enabled
    in config. Used by the job dispatcher to route function calls to the correct
    engine (RunspacePool for PS 7.x cloud functions, SessionPool for PS 5.1 on-prem).
#>

function Initialize-ServiceConnections {
    <#
    .SYNOPSIS
        Validates enabled services and builds function-to-engine mapping.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    $registry = @{
        CloudServicesEnabled  = $false
        OnPremServicesEnabled = $false
        FunctionEngineMap     = @{}  # FunctionName -> 'RunspacePool' | 'SessionPool'
        EnabledServices       = @()
    }

    $sc = $Config.ServiceConnections

    # Cloud services (PS 7.x RunspacePool)
    if ($sc.entra.enabled -or $sc.exchangeOnline.enabled) {
        $registry.CloudServicesEnabled = $true
        if ($sc.entra.enabled) { $registry.EnabledServices += 'entra' }
        if ($sc.exchangeOnline.enabled) { $registry.EnabledServices += 'exchangeOnline' }

        # Map StandardFunctions exports to RunspacePool
        $standardManifest = Join-Path $Config.StandardModulePath 'StandardFunctions.psd1'
        if (Test-Path $standardManifest) {
            $manifest = Import-PowerShellDataFile -Path $standardManifest
            foreach ($fn in $manifest.FunctionsToExport) {
                $registry.FunctionEngineMap[$fn] = 'RunspacePool'
            }
        }
    }

    # On-prem services (PS 5.1 SessionPool)
    $onPremServices = @('activeDirectory', 'exchangeServer', 'sharepointOnline', 'teams')
    foreach ($svc in $onPremServices) {
        if ($sc.$svc.enabled) {
            $registry.OnPremServicesEnabled = $true
            $registry.EnabledServices += $svc
        }
    }

    if ($registry.OnPremServicesEnabled) {
        # Map HybridFunctions exports to SessionPool
        $hybridManifest = Join-Path $Config.HybridModulePath 'HybridFunctions.psd1'
        if (Test-Path $hybridManifest) {
            $manifest = Import-PowerShellDataFile -Path $hybridManifest
            foreach ($fn in $manifest.FunctionsToExport) {
                $registry.FunctionEngineMap[$fn] = 'SessionPool'
            }
        }
    }

    # Custom modules — check PrivateData.ExecutionEngine for engine hint
    if (Test-Path $Config.CustomModulesPath) {
        $customDirs = Get-ChildItem -Path $Config.CustomModulesPath -Directory -ErrorAction SilentlyContinue
        foreach ($dir in $customDirs) {
            $psd1 = Join-Path $dir.FullName "$($dir.Name).psd1"
            if (Test-Path $psd1) {
                $manifest = Import-PowerShellDataFile -Path $psd1
                $engine = $manifest.PrivateData.ExecutionEngine ?? 'RunspacePool'
                foreach ($fn in $manifest.FunctionsToExport) {
                    $registry.FunctionEngineMap[$fn] = $engine
                }
            }
        }
    }

    Write-WorkerLog -Message "Service connections initialized. Enabled: $($registry.EnabledServices -join ', ')" -Properties @{
        CloudEnabled  = $registry.CloudServicesEnabled
        OnPremEnabled = $registry.OnPremServicesEnabled
        FunctionCount = $registry.FunctionEngineMap.Count
    }

    return [PSCustomObject]$registry
}

function Get-FunctionEngine {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FunctionName,

        [Parameter(Mandatory)]
        [PSCustomObject]$ServiceRegistry
    )

    if ($ServiceRegistry.FunctionEngineMap.ContainsKey($FunctionName)) {
        return $ServiceRegistry.FunctionEngineMap[$FunctionName]
    }
    return $null  # Not in whitelist
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
    foreach ($fn in $ServiceRegistry.FunctionEngineMap.Keys) {
        [void]$allowed.Add($fn)
    }
    return $allowed
}
