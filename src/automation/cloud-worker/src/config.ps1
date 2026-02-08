<#
.SYNOPSIS
    Configuration loader for the PowerShell Cloud Worker.
.DESCRIPTION
    Reads configuration from environment variables and validates required values.
#>

function Get-WorkerConfiguration {
    [CmdletBinding()]
    param()

    $config = [PSCustomObject]@{
        WorkerId                   = $env:WORKER_ID
        MaxParallelism             = [int]($env:MAX_PARALLELISM ?? '2')
        ServiceBusNamespace        = $env:SERVICE_BUS_NAMESPACE
        JobsTopicName              = $env:JOBS_TOPIC_NAME ?? 'worker-jobs'
        ResultsTopicName           = $env:RESULTS_TOPIC_NAME ?? 'worker-results'
        KeyVaultName               = $env:KEY_VAULT_NAME
        TargetTenantId             = $env:TARGET_TENANT_ID
        AppId                      = $env:APP_ID
        CertificateName            = $env:CERT_NAME ?? 'worker-app-cert'
        AppInsightsConnectionString = $env:APPINSIGHTS_CONNECTION_STRING
        DotNetLibPath              = $env:DOTNET_LIB_PATH ?? '/opt/dotnet-libs'
        CustomModulesPath          = $env:CUSTOM_MODULES_PATH ?? '/app/modules/CustomFunctions'
        StandardModulePath         = $env:STANDARD_MODULE_PATH ?? '/app/modules/StandardFunctions'
        MaxRetryCount              = [int]($env:MAX_RETRY_COUNT ?? '5')
        BaseRetryDelaySeconds      = [int]($env:BASE_RETRY_DELAY_SECONDS ?? '2')
        MaxRetryDelaySeconds       = [int]($env:MAX_RETRY_DELAY_SECONDS ?? '120')
        HealthCheckIntervalSeconds = [int]($env:HEALTH_CHECK_INTERVAL_SECONDS ?? '30')
        IdleTimeoutSeconds         = [int]($env:IDLE_TIMEOUT_SECONDS ?? '300')
        ShutdownGraceSeconds       = [int]($env:SHUTDOWN_GRACE_SECONDS ?? '30')
    }

    $requiredFields = @(
        'WorkerId',
        'ServiceBusNamespace',
        'KeyVaultName',
        'TargetTenantId',
        'AppId'
    )

    $missing = @()
    foreach ($field in $requiredFields) {
        if ([string]::IsNullOrWhiteSpace($config.$field)) {
            $missing += $field
        }
    }

    if ($missing.Count -gt 0) {
        throw "Missing required configuration: $($missing -join ', '). Set the corresponding environment variables."
    }

    if ($config.MaxParallelism -lt 1 -or $config.MaxParallelism -gt 20) {
        throw "MAX_PARALLELISM must be between 1 and 20. Got: $($config.MaxParallelism)"
    }

    return $config
}
