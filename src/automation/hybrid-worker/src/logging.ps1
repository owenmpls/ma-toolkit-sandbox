<#
.SYNOPSIS
    Application Insights logging for the Hybrid Worker.
.DESCRIPTION
    Provides structured logging via the Application Insights TelemetryClient.
    Falls back to console logging when App Insights is not configured.
#>

function Initialize-WorkerLogging {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    $script:WorkerId = $Config.WorkerId
    $script:LoggingInitialized = $false
    $script:TelemetryClient = $null

    if (-not [string]::IsNullOrWhiteSpace($Config.AppInsightsConnectionString)) {
        try {
            $aiDllPath = Join-Path $Config.DotNetLibPath 'Microsoft.ApplicationInsights.dll'
            if (Test-Path $aiDllPath) {
                Add-Type -Path $aiDllPath -ErrorAction Stop

                $telemetryConfig = [Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration]::CreateDefault()
                $telemetryConfig.ConnectionString = $Config.AppInsightsConnectionString

                $script:TelemetryClient = [Microsoft.ApplicationInsights.TelemetryClient]::new($telemetryConfig)
                $script:TelemetryClient.Context.Cloud.RoleName = 'hybrid-worker'
                $script:TelemetryClient.Context.Cloud.RoleInstance = $Config.WorkerId
                $script:LoggingInitialized = $true

                Write-Host "[INFO] Application Insights initialized for worker '$($Config.WorkerId)'"
            }
            else {
                Write-Warning "Application Insights DLL not found at '$aiDllPath'. Using console logging only."
            }
        }
        catch {
            Write-Warning "Failed to initialize Application Insights: $($_.Exception.Message). Using console logging only."
        }
    }
    else {
        Write-Host "[INFO] No App Insights connection string configured. Using console logging only."
    }
}

function Write-WorkerLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [ValidateSet('Verbose', 'Information', 'Warning', 'Error', 'Critical')]
        [string]$Severity = 'Information',

        [hashtable]$Properties = @{}
    )

    $Properties['WorkerId'] = $script:WorkerId
    $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    $consolePrefix = "[$($Severity.ToUpper().Substring(0, 4))]"

    # Console output
    switch ($Severity) {
        'Error'    { Write-Host "$timestamp $consolePrefix $Message" -ForegroundColor Red }
        'Critical' { Write-Host "$timestamp $consolePrefix $Message" -ForegroundColor Red }
        'Warning'  { Write-Host "$timestamp $consolePrefix $Message" -ForegroundColor Yellow }
        default    { Write-Host "$timestamp $consolePrefix $Message" }
    }

    # App Insights
    if ($script:LoggingInitialized -and $null -ne $script:TelemetryClient) {
        $severityLevel = switch ($Severity) {
            'Verbose'     { [Microsoft.ApplicationInsights.DataContracts.SeverityLevel]::Verbose }
            'Information' { [Microsoft.ApplicationInsights.DataContracts.SeverityLevel]::Information }
            'Warning'     { [Microsoft.ApplicationInsights.DataContracts.SeverityLevel]::Warning }
            'Error'       { [Microsoft.ApplicationInsights.DataContracts.SeverityLevel]::Error }
            'Critical'    { [Microsoft.ApplicationInsights.DataContracts.SeverityLevel]::Critical }
        }

        $traceTelemetry = [Microsoft.ApplicationInsights.DataContracts.TraceTelemetry]::new($Message, $severityLevel)
        foreach ($key in $Properties.Keys) {
            $traceTelemetry.Properties[$key] = [string]$Properties[$key]
        }
        $script:TelemetryClient.TrackTrace($traceTelemetry)
    }
}

function Write-WorkerException {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception,

        [hashtable]$Properties = @{}
    )

    $Properties['WorkerId'] = $script:WorkerId
    Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') [EXCP] $($Exception.GetType().Name): $($Exception.Message)" -ForegroundColor Red

    if ($script:LoggingInitialized -and $null -ne $script:TelemetryClient) {
        $exceptionTelemetry = [Microsoft.ApplicationInsights.DataContracts.ExceptionTelemetry]::new($Exception)
        foreach ($key in $Properties.Keys) {
            $exceptionTelemetry.Properties[$key] = [string]$Properties[$key]
        }
        $script:TelemetryClient.TrackException($exceptionTelemetry)
    }
}

function Write-WorkerMetric {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [double]$Value,

        [hashtable]$Properties = @{}
    )

    $Properties['WorkerId'] = $script:WorkerId

    if ($script:LoggingInitialized -and $null -ne $script:TelemetryClient) {
        $metricTelemetry = [Microsoft.ApplicationInsights.DataContracts.MetricTelemetry]::new($Name, $Value)
        foreach ($key in $Properties.Keys) {
            $metricTelemetry.Properties[$key] = [string]$Properties[$key]
        }
        $script:TelemetryClient.TrackMetric($metricTelemetry)
    }
}

function Write-WorkerEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$EventName,

        [hashtable]$Properties = @{},
        [hashtable]$Metrics = @{}
    )

    $Properties['WorkerId'] = $script:WorkerId
    Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') [EVNT] $EventName" -ForegroundColor Cyan

    if ($script:LoggingInitialized -and $null -ne $script:TelemetryClient) {
        $eventTelemetry = [Microsoft.ApplicationInsights.DataContracts.EventTelemetry]::new($EventName)
        foreach ($key in $Properties.Keys) {
            $eventTelemetry.Properties[$key] = [string]$Properties[$key]
        }
        foreach ($key in $Metrics.Keys) {
            $eventTelemetry.Metrics[$key] = [double]$Metrics[$key]
        }
        $script:TelemetryClient.TrackEvent($eventTelemetry)
    }
}

function Flush-WorkerTelemetry {
    [CmdletBinding()]
    param()

    if ($script:LoggingInitialized -and $null -ne $script:TelemetryClient) {
        $script:TelemetryClient.Flush()
        # Allow time for flush to complete
        Start-Sleep -Milliseconds 500
    }
}
