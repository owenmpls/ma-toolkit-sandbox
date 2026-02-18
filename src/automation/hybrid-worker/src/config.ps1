<#
.SYNOPSIS
    Configuration loader for the Hybrid Worker.
.DESCRIPTION
    Reads configuration from a JSON file with environment variable overrides.
    The config file path is set by the .NET service host via HYBRID_WORKER_CONFIG_PATH.
#>

function Get-WorkerConfiguration {
    [CmdletBinding()]
    param(
        [string]$ConfigPath = ($env:HYBRID_WORKER_CONFIG_PATH ??
            'C:\ProgramData\MaToolkit\HybridWorker\config\worker-config.json')
    )

    if (-not (Test-Path $ConfigPath)) {
        throw "Configuration file not found: $ConfigPath"
    }

    $json = Get-Content -Path $ConfigPath -Raw | ConvertFrom-Json

    # Build config object with defaults, allowing env var overrides
    $config = [PSCustomObject]@{
        WorkerId                   = ($env:HYBRID_WORKER_ID ?? $json.workerId)
        MaxParallelism             = [int]($env:HYBRID_WORKER_MAX_PARALLELISM ?? $json.maxParallelism ?? '2')
        MaxPs51Sessions            = [int]($env:HYBRID_WORKER_MAX_PS51_SESSIONS ?? $json.maxPs51Sessions ?? '2')
        ServiceBusNamespace        = ($env:HYBRID_WORKER_SB_NAMESPACE ?? $json.serviceBus.namespace)
        JobsTopicName              = ($env:HYBRID_WORKER_JOBS_TOPIC ?? $json.serviceBus.jobsTopicName ?? 'worker-jobs')
        ResultsTopicName           = ($env:HYBRID_WORKER_RESULTS_TOPIC ?? $json.serviceBus.resultsTopicName ?? 'worker-results')
        AuthTenantId               = ($json.auth.tenantId)
        AuthAppId                  = ($json.auth.appId)
        AuthCertificateThumbprint  = ($json.auth.certificateThumbprint)
        KeyVaultName               = ($json.auth.keyVaultName)
        TargetTenantId             = ($json.targetTenant.tenantId)
        TargetOrganization         = ($json.targetTenant.organization)
        TargetAppId                = ($json.targetTenant.appId)
        TargetCertificateName      = ($json.targetTenant.certificateName ?? 'worker-app-cert')
        ServiceConnections         = $json.serviceConnections
        AppInsightsConnectionString = ($json.appInsights.connectionString)
        DotNetLibPath              = (Join-Path $PSScriptRoot '..\dotnet-libs')
        StandardModulePath         = (Join-Path $PSScriptRoot '..\modules\StandardFunctions')
        HybridModulePath           = (Join-Path $PSScriptRoot '..\modules\HybridFunctions')
        CustomModulesPath          = (Join-Path $PSScriptRoot '..\modules\CustomFunctions')
        UpdateEnabled              = [bool]($json.update.enabled ?? $true)
        UpdateStorageAccount       = ($json.update.storageAccountName)
        UpdateContainerName        = ($json.update.containerName ?? 'hybrid-worker')
        UpdatePollIntervalMinutes  = [int]($json.update.pollIntervalMinutes ?? '5')
        MaxRetryCount              = [int]($json.maxRetryCount ?? '5')
        BaseRetryDelaySeconds      = [int]($json.baseRetryDelaySeconds ?? '2')
        MaxRetryDelaySeconds       = [int]($json.maxRetryDelaySeconds ?? '120')
        IdleTimeoutSeconds         = [int]($json.idleTimeoutSeconds ?? '0')
        ShutdownGraceSeconds       = [int]($json.shutdownGraceSeconds ?? '30')
        HealthCheckPort            = [int]($json.healthCheckPort ?? '8080')
        LogPath                    = ($json.logPath ?? 'C:\ProgramData\MaToolkit\HybridWorker\logs')
        InstallPath                = ($env:HYBRID_WORKER_INSTALL_PATH ?? 'C:\ProgramData\MaToolkit\HybridWorker')
    }

    # Validate required fields
    $requiredFields = @('WorkerId', 'ServiceBusNamespace', 'AuthTenantId', 'AuthAppId',
                        'AuthCertificateThumbprint', 'KeyVaultName')
    $missing = @()
    foreach ($field in $requiredFields) {
        if ([string]::IsNullOrWhiteSpace($config.$field)) { $missing += $field }
    }
    if ($missing.Count -gt 0) {
        throw "Missing required configuration: $($missing -join ', ')"
    }

    # Validate conditional requirements
    $cloudServicesEnabled = ($config.ServiceConnections.entra.enabled -eq $true) -or
                            ($config.ServiceConnections.exchangeOnline.enabled -eq $true)
    if ($cloudServicesEnabled) {
        if ([string]::IsNullOrWhiteSpace($config.TargetTenantId)) { throw 'targetTenant.tenantId required when entra or exchangeOnline enabled' }
        if ([string]::IsNullOrWhiteSpace($config.TargetAppId)) { throw 'targetTenant.appId required when entra or exchangeOnline enabled' }
    }

    # Validate ranges
    if ($config.MaxParallelism -lt 1 -or $config.MaxParallelism -gt 20) {
        throw "maxParallelism must be between 1 and 20. Got: $($config.MaxParallelism)"
    }
    if ($config.MaxPs51Sessions -lt 1 -or $config.MaxPs51Sessions -gt 10) {
        throw "maxPs51Sessions must be between 1 and 10. Got: $($config.MaxPs51Sessions)"
    }

    return $config
}
