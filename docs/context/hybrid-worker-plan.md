# Hybrid-Worker Implementation Plan

## Context

The **cloud-worker** (`src/automation/cloud-worker/`) is a containerized PowerShell 7.4 worker running in Azure Container Apps. It processes migration jobs received via Azure Service Bus, executing Entra ID (Microsoft Graph) and Exchange Online functions using a RunspacePool for parallel execution. It authenticates to Azure resources via managed identity and to the target M365 tenant via certificate-based service principal auth.

The **hybrid-worker** is a new component that runs on on-premises Windows servers or Windows VMs in other tenants/subscriptions. It handles services that require PowerShell 5.1 — Active Directory (RSAT), Exchange Server Management Shell, SharePoint Online Management Shell, and Microsoft Teams — while also supporting PS 7.x cloud modules (MgGraph, EXO). It must:

- Use the **same Service Bus topics and job/result data contracts** as the cloud-worker
- Support **configurable service connections** (not all instances connect to all services)
- Be **easy to deploy** with a setup script and native Windows Service installation
- **Self-update** when CI/CD pushes new versions to Azure Blob Storage
- Authenticate to Azure resources (Service Bus, Key Vault, Blob Storage) via **service principal with certificate**
- Write telemetry to **Application Insights** using the same pattern as the cloud-worker
- Run as a **native Windows Service** under a Group Managed Service Account (gMSA) with Windows Event Log integration

### Architectural Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| PS 5.1 execution | **PSSession pool** | Built-in PowerShell Remoting to localhost Windows PowerShell 5.1. Persistent sessions keep modules loaded. No custom IPC protocol needed. |
| Self-update | **Azure Blob Storage** | CI/CD uploads zip + `version.json` manifest. Worker polls periodically. Uses same SP cert auth already needed for SB/KV. |
| Service host | **.NET Worker Service** | Native Windows Service via `Microsoft.Extensions.Hosting.WindowsServices`. No third-party binaries. Supports gMSA, Windows Event Log, service recovery, and code signing. Installed with native `sc.exe` / `New-Service`. |

---

## 1. Directory Structure

```
src/automation/hybrid-worker/
  CLAUDE.md                                  # Project context for Claude Code
  Install-HybridWorker.ps1                   # First-time setup (run as admin)
  Uninstall-HybridWorker.ps1                 # Clean removal
  version.txt                                # Current version (e.g., "1.0.0")
  service-host/                              # .NET 8 Worker Service (Windows Service host)
    HybridWorker.ServiceHost.csproj
    Program.cs                               # Host builder with UseWindowsService()
    WorkerProcessService.cs                  # BackgroundService managing pwsh.exe process
    appsettings.json                         # Service host configuration
  src/
    worker.ps1                               # Main entry point (adapted from cloud-worker)
    config.ps1                               # JSON config file loader + env var overrides
    logging.ps1                              # App Insights telemetry (reused from cloud-worker)
    auth.ps1                                 # SP cert auth for Azure + on-prem credential retrieval
    service-bus.ps1                          # SB integration (adapted: ClientCertificateCredential)
    runspace-manager.ps1                     # PS 7.x RunspacePool (reused from cloud-worker)
    session-pool.ps1                         # NEW: PSSession pool for PS 5.1 functions
    job-dispatcher.ps1                       # Adapted: dual-engine routing (runspace vs session)
    service-connections.ps1                  # NEW: configurable service registry + function routing
    update-manager.ps1                       # NEW: blob storage version check + zip download
    health-check.ps1                         # Health endpoint (adapted from cloud-worker)
  modules/
    StandardFunctions/                       # Copy of cloud-worker StandardFunctions (Entra + EXO)
      StandardFunctions.psd1
      StandardFunctions.psm1
      EntraFunctions.ps1
      ExchangeFunctions.ps1
    HybridFunctions/                         # NEW: on-prem functions (PS 5.1 via PSSession)
      HybridFunctions.psd1
      HybridFunctions.psm1
      ADFunctions.ps1                        # Active Directory functions
      ExchangeServerFunctions.ps1            # Exchange Server Management Shell functions
      SPOFunctions.ps1                       # SharePoint Online functions
      TeamsFunctions.ps1                     # Microsoft Teams functions
    CustomFunctions/                         # Customer-specific modules (same pattern)
  config/
    worker-config.example.json               # Example configuration file
  tests/
    Test-WorkerLocal.ps1                     # Parse + structure validation tests
  dotnet-libs/                               # .NET assemblies (same versions as cloud-worker)
    Azure.Messaging.ServiceBus.dll           # 7.18.1
    Azure.Core.dll
    Azure.Core.Amqp.dll
    Azure.Identity.dll                       # 1.13.1
    Microsoft.ApplicationInsights.dll        # 2.22.0
    System.Memory.Data.dll
    System.ClientModel.dll
    Microsoft.Identity.Client.dll
    Microsoft.Bcl.AsyncInterfaces.dll
    System.Diagnostics.DiagnosticSource.dll
```

### Installation directory layout (on target server)

```
C:\ProgramData\MaToolkit\HybridWorker\
  current\                                    # Active version (worker files)
    src\
    modules\
    dotnet-libs\
    service-host\                             # Published .NET Worker Service binary
      HybridWorker.ServiceHost.exe            # Self-contained .NET 8 executable
      appsettings.json
    version.txt
  staging\                                    # Downloaded update (before apply)
  previous\                                   # Previous version (one-step rollback)
  config\
    worker-config.json                        # Configuration (persists across updates)
  logs\
    worker.log                                # PowerShell worker stdout/stderr
    service-host.log                          # .NET service host log
```

---

## 2. Service Host (`service-host/`) — .NET Worker Service

A thin .NET 8 Worker Service that runs as a native Windows Service. It manages the PowerShell worker process (`pwsh.exe -NoProfile -File worker.ps1`) and provides:

- **Native Windows Service lifecycle** — `sc.exe` / `Start-Service` / `Stop-Service`
- **gMSA support** — runs under `Log on as` configured via `sc.exe` or Group Policy
- **Windows Event Log** — startup, shutdown, crashes, and restarts written to `Application` log
- **Service recovery** — configured via `sc.exe failurereset` / `sc.exe failure` for auto-restart
- **Graceful shutdown** — sends `Ctrl+C` to the PowerShell process, waits for grace period, then terminates
- **Process monitoring** — restarts the worker if it exits unexpectedly
- **Log rotation** — redirects worker stdout/stderr to log files with size-based rotation

### `HybridWorker.ServiceHost.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <AssemblyName>HybridWorker.ServiceHost</AssemblyName>
    <RootNamespace>MaToolkit.HybridWorker.ServiceHost</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="8.0.1" />
  </ItemGroup>
</Project>
```

### `Program.cs`

```csharp
using MaToolkit.HybridWorker.ServiceHost;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MaToolkitHybridWorker";
});
builder.Services.AddHostedService<WorkerProcessService>();

var host = builder.Build();
host.Run();
```

### `WorkerProcessService.cs`

```csharp
using System.Diagnostics;

namespace MaToolkit.HybridWorker.ServiceHost;

public class WorkerProcessService : BackgroundService
{
    private readonly ILogger<WorkerProcessService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _installPath;
    private readonly int _shutdownGraceSeconds;
    private Process? _workerProcess;

    public WorkerProcessService(
        ILogger<WorkerProcessService> logger,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration)
    {
        _logger = logger;
        _lifetime = lifetime;
        _installPath = configuration.GetValue<string>("InstallPath")
            ?? @"C:\ProgramData\MaToolkit\HybridWorker";
        _shutdownGraceSeconds = configuration.GetValue<int>("ShutdownGraceSeconds", 45);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Hybrid Worker service host starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var workerScript = Path.Combine(_installPath, "current", "src", "worker.ps1");
            var logPath = Path.Combine(_installPath, "logs", "worker.log");

            if (!File.Exists(workerScript))
            {
                _logger.LogError("Worker script not found: {Path}", workerScript);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            _logger.LogInformation("Starting PowerShell worker: {Script}", workerScript);

            var logStream = new StreamWriter(logPath, append: true) { AutoFlush = true };

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = $"-NoProfile -NonInteractive -File \"{workerScript}\"",
                WorkingDirectory = Path.Combine(_installPath, "current"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["HYBRID_WORKER_CONFIG_PATH"] = Path.Combine(
                        _installPath, "config", "worker-config.json"),
                    ["HYBRID_WORKER_INSTALL_PATH"] = _installPath
                }
            };

            _workerProcess = new Process { StartInfo = startInfo };
            _workerProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    logStream.WriteLine($"{DateTime.UtcNow:O} [OUT] {e.Data}");
                }
            };
            _workerProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    logStream.WriteLine($"{DateTime.UtcNow:O} [ERR] {e.Data}");
                    _logger.LogWarning("Worker stderr: {Message}", e.Data);
                }
            };

            _workerProcess.Start();
            _workerProcess.BeginOutputReadLine();
            _workerProcess.BeginErrorReadLine();

            _logger.LogInformation("Worker process started (PID: {PID}).", _workerProcess.Id);

            // Wait for process exit or cancellation
            try
            {
                await _workerProcess.WaitForExitAsync(stoppingToken);

                var exitCode = _workerProcess.ExitCode;
                _logger.LogInformation("Worker process exited with code {ExitCode}.", exitCode);

                // Exit code 0 = clean shutdown (e.g., self-update staged). Restart.
                // Exit code 100 = update applied, restart immediately.
                // Other = unexpected, wait before restarting.
                if (exitCode != 0 && exitCode != 100)
                {
                    _logger.LogWarning(
                        "Unexpected exit code {ExitCode}. Waiting 10s before restart.",
                        exitCode);
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Service is stopping — send graceful shutdown signal
                _logger.LogInformation(
                    "Service stop requested. Sending Ctrl+C to worker (grace: {Seconds}s).",
                    _shutdownGraceSeconds);

                if (!_workerProcess.HasExited)
                {
                    // GenerateConsoleCtrlEvent requires the process to share our console.
                    // Since we redirect streams, we use taskkill with /T to signal the tree.
                    // The worker registers Console.CancelKeyPress to set WorkerRunning = false.
                    try
                    {
                        // Send Ctrl+Break to the process tree
                        using var killProc = Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/PID {_workerProcess.Id} /T",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        killProc?.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send graceful shutdown signal.");
                    }

                    // Wait for graceful shutdown
                    using var graceCts = new CancellationTokenSource(
                        TimeSpan.FromSeconds(_shutdownGraceSeconds));
                    try
                    {
                        await _workerProcess.WaitForExitAsync(graceCts.Token);
                        _logger.LogInformation("Worker exited gracefully.");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning(
                            "Worker did not exit within grace period. Terminating.");
                        _workerProcess.Kill(entireProcessTree: true);
                    }
                }
            }
            finally
            {
                logStream.Dispose();
                _workerProcess?.Dispose();
                _workerProcess = null;
            }
        }

        _logger.LogInformation("Hybrid Worker service host stopped.");
    }
}
```

### `appsettings.json`

```json
{
  "InstallPath": "C:\\ProgramData\\MaToolkit\\HybridWorker",
  "ShutdownGraceSeconds": 45,
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    },
    "EventLog": {
      "SourceName": "MaToolkitHybridWorker",
      "LogName": "Application",
      "LogLevel": {
        "Default": "Information"
      }
    }
  }
}
```

---

## 3. Configuration (`config.ps1`)

### Source: JSON config file with environment variable overrides

Unlike the cloud-worker which uses environment variables exclusively (container pattern), the hybrid-worker uses a JSON configuration file as the primary source. Environment variables with `HYBRID_WORKER_` prefix can override any setting.

### Config file: `C:\ProgramData\MaToolkit\HybridWorker\config\worker-config.json`

```json
{
  "workerId": "hybrid-worker-01",
  "maxParallelism": 4,
  "maxPs51Sessions": 2,

  "serviceBus": {
    "namespace": "matoolkit-sbus.servicebus.windows.net",
    "jobsTopicName": "worker-jobs",
    "resultsTopicName": "worker-results"
  },

  "auth": {
    "tenantId": "hosting-tenant-guid",
    "appId": "sp-client-id-for-azure-resources",
    "certificateThumbprint": "ABC123DEF456...",
    "keyVaultName": "matoolkit-kv"
  },

  "targetTenant": {
    "tenantId": "target-tenant-guid",
    "organization": "target.onmicrosoft.com",
    "appId": "target-sp-client-id",
    "certificateName": "worker-app-cert"
  },

  "serviceConnections": {
    "entra": { "enabled": false },
    "exchangeOnline": { "enabled": false },
    "activeDirectory": {
      "enabled": true,
      "domainController": "dc01.corp.contoso.com",
      "credentialSecret": "ad-service-account"
    },
    "exchangeServer": {
      "enabled": true,
      "connectionUri": "http://exchange01.corp.contoso.com/PowerShell/",
      "credentialSecret": "exchange-service-account"
    },
    "sharepointOnline": {
      "enabled": false,
      "adminUrl": "https://contoso-admin.sharepoint.com"
    },
    "teams": { "enabled": false }
  },

  "appInsights": {
    "connectionString": "InstrumentationKey=..."
  },

  "update": {
    "enabled": true,
    "storageAccountName": "yourstorageaccount",
    "containerName": "hybrid-worker",
    "pollIntervalMinutes": 5
  },

  "idleTimeoutSeconds": 0,
  "shutdownGraceSeconds": 30,
  "healthCheckPort": 8080,
  "logPath": "C:\\ProgramData\\MaToolkit\\HybridWorker\\logs"
}
```

### Configuration fields

| Field | Type | Default | Required | Description |
|-------|------|---------|----------|-------------|
| `workerId` | string | — | Yes | Unique worker ID. Used as SB subscription filter and telemetry tag. |
| `maxParallelism` | int | 2 | No | RunspacePool size for PS 7.x functions (1-20). |
| `maxPs51Sessions` | int | 2 | No | PSSession pool size for PS 5.1 functions (1-10). |
| `serviceBus.namespace` | string | — | Yes | Service Bus namespace FQDN. |
| `serviceBus.jobsTopicName` | string | `worker-jobs` | No | Inbound job topic name. |
| `serviceBus.resultsTopicName` | string | `worker-results` | No | Outbound result topic name. |
| `auth.tenantId` | string | — | Yes | Hosting tenant ID (where Azure resources live). |
| `auth.appId` | string | — | Yes | Service principal client ID for Azure resource auth. |
| `auth.certificateThumbprint` | string | — | Yes | Thumbprint of cert in `Cert:\LocalMachine\My`. |
| `auth.keyVaultName` | string | — | Yes | Key Vault name for cert/credential retrieval. |
| `targetTenant.tenantId` | string | — | Conditional | Required if `entra` or `exchangeOnline` enabled. |
| `targetTenant.organization` | string | — | Conditional | Required if `exchangeOnline` enabled. |
| `targetTenant.appId` | string | — | Conditional | Required if `entra` or `exchangeOnline` enabled. |
| `targetTenant.certificateName` | string | `worker-app-cert` | No | KV certificate name for target tenant auth. |
| `serviceConnections.*` | object | — | No | Per-service enable/disable and connection params. |
| `appInsights.connectionString` | string | — | No | If absent, console-only logging. |
| `update.enabled` | bool | true | No | Enable self-update polling. |
| `update.storageAccountName` | string | — | Conditional | Required if `update.enabled`. |
| `update.containerName` | string | `hybrid-worker` | No | Blob container name. |
| `update.pollIntervalMinutes` | int | 5 | No | How often to check for updates. |
| `idleTimeoutSeconds` | int | 0 | No | 0 = disabled (persistent service). |
| `shutdownGraceSeconds` | int | 30 | No | Grace period for active jobs on shutdown. |
| `healthCheckPort` | int | 8080 | No | HTTP health endpoint port. |
| `logPath` | string | `logs\` | No | Directory for log files. |

### `Get-WorkerConfiguration` function

```powershell
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
```

**Differences from cloud-worker `config.ps1`:** Cloud-worker reads exclusively from env vars (lines 12-33 of `src/automation/cloud-worker/src/config.ps1`). Hybrid-worker reads a JSON file as primary source with env var overrides. The `HYBRID_WORKER_CONFIG_PATH` and `HYBRID_WORKER_INSTALL_PATH` env vars are set by the .NET service host when it launches `pwsh.exe`. Same validation pattern.

---

## 4. Authentication (`auth.ps1`)

Three-layer authentication model, adapted from cloud-worker `src/automation/cloud-worker/src/auth.ps1`.

### Layer 1: Azure resource auth (Service Bus, Key Vault, Blob Storage)

**Cloud-worker:** `Connect-AzAccount -Identity` (managed identity, line 22)
**Hybrid-worker:** `Connect-AzAccount -ServicePrincipal -CertificateThumbprint`

```powershell
function Connect-HybridWorkerAzure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    Write-WorkerLog -Message 'Connecting to Azure with service principal certificate...'

    # Verify certificate exists in store
    $cert = Get-Item "Cert:\LocalMachine\My\$($Config.AuthCertificateThumbprint)" -ErrorAction SilentlyContinue
    if (-not $cert) {
        throw "Certificate with thumbprint '$($Config.AuthCertificateThumbprint)' not found in Cert:\LocalMachine\My"
    }

    Connect-AzAccount -ServicePrincipal `
        -CertificateThumbprint $Config.AuthCertificateThumbprint `
        -ApplicationId $Config.AuthAppId `
        -Tenant $Config.AuthTenantId `
        -ErrorAction Stop | Out-Null

    Write-WorkerLog -Message "Connected to Azure as SP '$($Config.AuthAppId)' in tenant '$($Config.AuthTenantId)'."
}
```

### Layer 2: Service Bus credential

**Cloud-worker:** `ManagedIdentityCredential` (in `service-bus.ps1`)
**Hybrid-worker:** `ClientCertificateCredential`

```powershell
function Get-ServiceBusCredential {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    $cert = Get-Item "Cert:\LocalMachine\My\$($Config.AuthCertificateThumbprint)"
    $credential = [Azure.Identity.ClientCertificateCredential]::new(
        $Config.AuthTenantId,
        $Config.AuthAppId,
        $cert
    )
    return $credential
}
```

### Layer 3: Target tenant auth (MgGraph + EXO) — reused verbatim

Functions `Get-WorkerCertificate` and `Get-RunspaceAuthScriptBlock` from cloud-worker `auth.ps1` lines 44-192 are **reused verbatim**. These retrieve the PFX from Key Vault and create a scriptblock that initializes MgGraph + EXO in each runspace using cert bytes.

### Layer 4: On-prem credential retrieval — NEW

```powershell
function Get-OnPremCredential {
    <#
    .SYNOPSIS
        Retrieves a username/password credential from Key Vault for on-prem service auth.
    .DESCRIPTION
        The KV secret stores a JSON object: { "username": "domain\\user", "password": "..." }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$KeyVaultName,

        [Parameter(Mandatory)]
        [string]$SecretName
    )

    Write-WorkerLog -Message "Retrieving on-prem credential '$SecretName' from Key Vault..."

    $secret = Get-AzKeyVaultSecret -VaultName $KeyVaultName -Name $SecretName -ErrorAction Stop
    $secretText = $secret.SecretValue | ConvertFrom-SecureString -AsPlainText
    $credData = $secretText | ConvertFrom-Json

    $securePassword = ConvertTo-SecureString $credData.password -AsPlainText -Force
    $credential = [System.Management.Automation.PSCredential]::new($credData.username, $securePassword)

    Write-WorkerLog -Message "Retrieved credential for '$($credData.username)'."
    return $credential
}
```

---

## 5. Service Bus (`service-bus.ps1`)

**Reused from:** `src/automation/cloud-worker/src/service-bus.ps1`

### Functions reused verbatim (no changes)

- `Initialize-ServiceBusAssemblies` — loads .NET DLLs from `$DotNetLibPath`
- `New-ServiceBusSender` — creates `ServiceBusSender` for a topic
- `New-ServiceBusReceiver` — creates `ServiceBusReceiver` for a topic subscription
- `Receive-ServiceBusMessages` — receives messages with PeekLock, handles transient reconnection
- `Complete-ServiceBusMessage` — completes a message after successful processing
- `Abandon-ServiceBusMessage` — abandons a message for retry
- `Send-ServiceBusResult` — sends result message with application properties (WorkerId, JobId, BatchId, Status)
- `ConvertFrom-ServiceBusMessage` — deserializes message body from JSON

### Function adapted: `New-ServiceBusClient`

Only change is the credential constructor:

```powershell
function New-ServiceBusClient {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Namespace,

        [Parameter(Mandatory)]
        [PSCustomObject]$Config  # NEW: accepts Config for cert lookup
    )

    # CHANGED: ClientCertificateCredential instead of ManagedIdentityCredential
    $credential = Get-ServiceBusCredential -Config $Config

    $clientOptions = [Azure.Messaging.ServiceBus.ServiceBusClientOptions]::new()
    $client = [Azure.Messaging.ServiceBus.ServiceBusClient]::new($Namespace, $credential, $clientOptions)

    # Pre-warm: acquire a token early to surface auth failures
    Write-WorkerLog -Message 'Pre-warming Service Bus authentication token...'
    try {
        $tokenContext = [Azure.Core.TokenRequestContext]::new(
            [string[]]@('https://servicebus.azure.net/.default')
        )
        $token = $credential.GetToken($tokenContext, [System.Threading.CancellationToken]::None)
        Write-WorkerLog -Message "Service Bus token acquired (expires: $($token.ExpiresOn))."
    }
    catch {
        Write-WorkerLog -Message "Token pre-warm failed: $($_.Exception.Message)" -Severity Warning
    }

    return $client
}
```

---

## 6. Logging (`logging.ps1`)

**Reused from:** `src/automation/cloud-worker/src/logging.ps1` — **verbatim** with one constant change.

### Change

Line 30 of cloud-worker `logging.ps1`:
```powershell
# Cloud-worker:
$script:TelemetryClient.Context.Cloud.RoleName = 'cloud-worker'

# Hybrid-worker:
$script:TelemetryClient.Context.Cloud.RoleName = 'hybrid-worker'
```

### Functions reused verbatim

- `Initialize-WorkerLogging` — creates `TelemetryClient` from App Insights connection string
- `Write-WorkerLog` — `TraceTelemetry` with severity level + console echo
- `Write-WorkerException` — `ExceptionTelemetry` + console echo in red
- `Write-WorkerMetric` — `MetricTelemetry` (name + value + properties)
- `Write-WorkerEvent` — `EventTelemetry` (name + properties + metrics) + console echo in cyan
- `Flush-WorkerTelemetry` — `$client.Flush()` + 500ms wait

---

## 7. RunspacePool Manager (`runspace-manager.ps1`)

**Reused from:** `src/automation/cloud-worker/src/runspace-manager.ps1` — **verbatim**.

This file manages the PS 7.x RunspacePool used for cloud service functions (Entra, Exchange Online).

### Functions reused verbatim

- `Initialize-RunspacePool` — creates pool, imports modules (MgGraph, EXO, StandardFunctions, CustomFunctions), authenticates each runspace in parallel using cert bytes
- `Invoke-InRunspace` — dispatches function call with inline retry/throttle wrapper (auth reconnection, exponential backoff + jitter)
- `Get-RunspaceResult` — collects async result, returns `{ Success, Result, Error }` shape
- `Close-RunspacePool` — `$pool.Close()` + `$pool.Dispose()`

### Conditional initialization

The RunspacePool is only initialized if cloud services are enabled (entra or exchangeOnline). If only on-prem services are configured, this phase is skipped entirely.

---

## 8. PSSession Pool (`session-pool.ps1`) — NEW

Manages a pool of persistent PowerShell Remoting sessions to the localhost Windows PowerShell 5.1 endpoint. Each session keeps its modules loaded across job invocations, avoiding cold-start overhead.

### Architecture

```
PS 7.x Main Process (worker.ps1)
  |
  |-- PSSession[0] --> localhost WinPS 5.1 (AD + Exchange Server modules loaded)
  |-- PSSession[1] --> localhost WinPS 5.1 (AD + Exchange Server modules loaded)
  |
  Managed by session-pool.ps1
```

### Functions

```powershell
function Initialize-SessionPool {
    <#
    .SYNOPSIS
        Creates PSSession pool to localhost Windows PowerShell 5.1.
    .DESCRIPTION
        Creates $Config.MaxPs51Sessions persistent PSSessions, loads enabled
        modules in each, and authenticates to on-prem services.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config,

        [Parameter(Mandatory)]
        [hashtable]$OnPremCredentials  # { 'ad-service-account' = [PSCredential]; ... }
    )

    Write-WorkerLog -Message "Initializing PS 5.1 session pool (size=$($Config.MaxPs51Sessions))..."

    $sessions = @()
    $failedCount = 0

    for ($i = 0; $i -lt $Config.MaxPs51Sessions; $i++) {
        try {
            # Create persistent session to localhost Windows PowerShell 5.1
            $session = New-PSSession -ComputerName localhost `
                -ConfigurationName 'Microsoft.PowerShell' `
                -ErrorAction Stop

            # Load modules based on enabled service connections
            $initResult = Invoke-Command -Session $session -ScriptBlock {
                param($ServiceConnections, $Credentials, $HybridModulePath)

                $loaded = @()

                # Active Directory
                if ($ServiceConnections.activeDirectory.enabled) {
                    Import-Module ActiveDirectory -ErrorAction Stop
                    $loaded += 'ActiveDirectory'
                }

                # Exchange Server Management Shell
                if ($ServiceConnections.exchangeServer.enabled) {
                    $uri = $ServiceConnections.exchangeServer.connectionUri
                    $cred = $Credentials['exchangeServer']
                    $exSession = New-PSSession -ConfigurationName Microsoft.Exchange `
                        -ConnectionUri $uri -Credential $cred -Authentication Kerberos -ErrorAction Stop
                    Import-PSSession $exSession -AllowClobber -DisableNameChecking | Out-Null
                    $loaded += 'ExchangeServer'
                }

                # SharePoint Online
                if ($ServiceConnections.sharepointOnline.enabled) {
                    Import-Module Microsoft.Online.SharePoint.PowerShell -DisableNameChecking -ErrorAction Stop
                    $cred = $Credentials['sharepointOnline']
                    Connect-SPOService -Url $ServiceConnections.sharepointOnline.adminUrl -Credential $cred -ErrorAction Stop
                    $loaded += 'SharePointOnline'
                }

                # Microsoft Teams
                if ($ServiceConnections.teams.enabled) {
                    Import-Module MicrosoftTeams -ErrorAction Stop
                    $cred = $Credentials['teams']
                    Connect-MicrosoftTeams -Credential $cred -ErrorAction Stop
                    $loaded += 'MicrosoftTeams'
                }

                # Import HybridFunctions module (the actual function implementations)
                $manifestPath = Join-Path $HybridModulePath 'HybridFunctions.psd1'
                if (Test-Path $manifestPath) {
                    Import-Module $manifestPath -ErrorAction Stop
                    $loaded += 'HybridFunctions'
                }

                return $loaded
            } -ArgumentList $Config.ServiceConnections, $OnPremCredentials, $Config.HybridModulePath

            Write-WorkerLog -Message "Session ${i}: Modules loaded: $($initResult -join ', ')" -Properties @{ SessionIndex = $i }

            $sessions += [PSCustomObject]@{
                Session = $session
                Index   = $i
                Busy    = $false
                Job     = $null  # PowerShell background job handle
            }
        }
        catch {
            $failedCount++
            Write-WorkerLog -Message "Session $i failed to initialize: $($_.Exception.Message)" -Severity Error
            Write-WorkerException -Exception $_.Exception -Properties @{ SessionIndex = $i }
        }
    }

    if ($sessions.Count -eq 0) {
        throw "All PS 5.1 sessions failed to initialize."
    }

    if ($failedCount -gt 0) {
        Write-WorkerLog -Message "$failedCount session(s) failed. Running with reduced PS 5.1 parallelism." -Severity Warning
    }

    Write-WorkerEvent -EventName 'SessionPoolInitialized' -Properties @{
        PoolSize       = $sessions.Count
        FailedSessions = $failedCount
    }

    return [PSCustomObject]@{
        Sessions         = $sessions
        MaxSessions      = $Config.MaxPs51Sessions
        ActiveSessions   = $sessions.Count
    }
}

function Invoke-InSession {
    <#
    .SYNOPSIS
        Dispatches a function call to an available PSSession.
    .RETURNS
        Async handle object with same shape as Invoke-InRunspace for unified result collection.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Pool,

        [Parameter(Mandatory)]
        [string]$FunctionName,

        [Parameter(Mandatory)]
        [hashtable]$Parameters,

        [int]$MaxRetries = 5,
        [int]$BaseDelaySeconds = 2,
        [int]$MaxDelaySeconds = 120
    )

    # Find an available session
    $slot = $Pool.Sessions | Where-Object { -not $_.Busy } | Select-Object -First 1
    if (-not $slot) {
        throw "No available PS 5.1 sessions in pool."
    }
    $slot.Busy = $true

    # Dispatch as a PowerShell background job using Invoke-Command -AsJob
    # The scriptblock includes retry/throttle logic adapted for PS 5.1 syntax
    $job = Invoke-Command -Session $slot.Session -AsJob -ScriptBlock {
        param($FunctionName, $Parameters, $MaxRetries, $BaseDelaySeconds, $MaxDelaySeconds)

        $attempt = 0
        while ($true) {
            $attempt++
            try {
                $result = & $FunctionName @Parameters
                return [PSCustomObject]@{
                    Success = $true
                    Result  = $result
                    Error   = $null
                }
            }
            catch {
                $ex = $_.Exception
                $innermost = $ex
                while ($innermost.InnerException) { $innermost = $innermost.InnerException }

                $errorMessage = if ($innermost.Message) { $innermost.Message }
                                elseif ($ex.Message) { $ex.Message }
                                else { $ex.GetType().FullName }
                $errorType = $innermost.GetType().FullName
                $matchText = "$($ex.Message) $($innermost.Message)"

                # Check for throttling
                $isThrottled = $false
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'please.*retry'
                )
                foreach ($pattern in $throttlePatterns) {
                    if ($matchText -match $pattern) {
                        $isThrottled = $true
                        break
                    }
                }

                if ($isThrottled -and $attempt -lt $MaxRetries) {
                    $retryAfter = 0
                    if ($matchText -match 'Retry-After[:\s]+(\d+)') {
                        $retryAfter = [int]$Matches[1]
                    }
                    if ($retryAfter -gt 0) {
                        Start-Sleep -Seconds $retryAfter
                    }
                    else {
                        $exp = [math]::Min($BaseDelaySeconds * [math]::Pow(2, $attempt - 1), $MaxDelaySeconds)
                        $jitter = Get-Random -Minimum 0.0 -Maximum ($exp * 0.3)
                        Start-Sleep -Seconds ([math]::Round($exp + $jitter, 1))
                    }
                    continue
                }

                return [PSCustomObject]@{
                    Success = $false
                    Result  = $null
                    Error   = [PSCustomObject]@{
                        Message     = $errorMessage
                        Type        = $errorType
                        IsThrottled = $isThrottled
                        Attempts    = $attempt
                    }
                }
            }
        }
    } -ArgumentList $FunctionName, $Parameters, $MaxRetries, $BaseDelaySeconds, $MaxDelaySeconds

    $slot.Job = $job

    # Return handle with same shape as Invoke-InRunspace for unified collection
    return [PSCustomObject]@{
        SessionSlot = $slot
        Job         = $job
        Engine      = 'SessionPool'
    }
}

function Get-SessionResult {
    <#
    .SYNOPSIS
        Collects result from a completed PSSession job.
    .RETURNS
        Same shape as Get-RunspaceResult: { Success, Result, Error }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$AsyncHandle
    )

    try {
        $output = Receive-Job -Job $AsyncHandle.Job -ErrorAction Stop
        Remove-Job -Job $AsyncHandle.Job -Force -ErrorAction SilentlyContinue

        if ($output -and $output.Count -gt 0) {
            return $output[-1]
        }

        return [PSCustomObject]@{
            Success = $true
            Result  = $null
            Error   = $null
        }
    }
    catch {
        Remove-Job -Job $AsyncHandle.Job -Force -ErrorAction SilentlyContinue
        return [PSCustomObject]@{
            Success = $false
            Result  = $null
            Error   = [PSCustomObject]@{
                Message     = $_.Exception.Message
                Type        = $_.Exception.GetType().FullName
                IsThrottled = $false
                Attempts    = 1
            }
        }
    }
    finally {
        # Release the session slot
        $AsyncHandle.SessionSlot.Busy = $false
        $AsyncHandle.SessionSlot.Job = $null
    }
}

function Test-SessionPoolHealth {
    <#
    .SYNOPSIS
        Tests session health and recreates dead sessions.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Pool
    )

    foreach ($slot in $Pool.Sessions) {
        if ($slot.Busy) { continue }  # Don't test busy sessions
        try {
            $result = Invoke-Command -Session $slot.Session -ScriptBlock { $true } -ErrorAction Stop
            if ($result -ne $true) { throw 'Health check returned unexpected result' }
        }
        catch {
            Write-WorkerLog -Message "Session $($slot.Index) is dead, recreating..." -Severity Warning
            try {
                Remove-PSSession -Session $slot.Session -ErrorAction SilentlyContinue
                $slot.Session = New-PSSession -ComputerName localhost -ConfigurationName 'Microsoft.PowerShell' -ErrorAction Stop
                # Re-initialize modules... (same init logic as Initialize-SessionPool)
                Write-WorkerLog -Message "Session $($slot.Index) recreated successfully."
            }
            catch {
                Write-WorkerLog -Message "Failed to recreate session $($slot.Index): $($_.Exception.Message)" -Severity Error
            }
        }
    }
}

function Close-SessionPool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Pool
    )

    Write-WorkerLog -Message 'Closing PS 5.1 session pool...'
    foreach ($slot in $Pool.Sessions) {
        try {
            if ($slot.Job) {
                Stop-Job -Job $slot.Job -ErrorAction SilentlyContinue
                Remove-Job -Job $slot.Job -Force -ErrorAction SilentlyContinue
            }
            Remove-PSSession -Session $slot.Session -ErrorAction SilentlyContinue
        }
        catch {
            Write-WorkerLog -Message "Error closing session $($slot.Index): $($_.Exception.Message)" -Severity Warning
        }
    }
    Write-WorkerLog -Message 'PS 5.1 session pool closed.'
}
```

---

## 9. Service Connections (`service-connections.ps1`) — NEW

Maps function names to execution engines based on which services are enabled in config.

### Service-to-engine mapping

| Service | Engine | PS Version | Auth Method |
|---------|--------|------------|-------------|
| `entra` | RunspacePool | 7.x | Cert from KV -> MgGraph |
| `exchangeOnline` | RunspacePool | 7.x | Cert from KV -> EXO |
| `activeDirectory` | SessionPool | 5.1 | AD cmdlets (domain-joined or explicit credential) |
| `exchangeServer` | SessionPool | 5.1 | Kerberos to Exchange PowerShell endpoint |
| `sharepointOnline` | SessionPool | 5.1 | Credential to SPO Management Shell |
| `teams` | SessionPool | 5.1 | Credential to MicrosoftTeams module |

### Functions

```powershell
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
```

---

## 10. Job Dispatcher (`job-dispatcher.ps1`)

**Adapted from:** `src/automation/cloud-worker/src/job-dispatcher.ps1`

### Functions reused verbatim

- `Test-JobMessage` (lines 9-40) — validates required fields
- `ConvertTo-ParameterHashtable` (lines 42-65) — PSCustomObject to hashtable
- `New-JobResult` (lines 67-123) — creates standardized result object

### Function adapted: `Start-JobDispatcher`

Key changes from cloud-worker version (lines 180-461):

1. **Dual active-job tracking** — separate lists for runspace and session jobs
2. **Function routing** — uses `Get-FunctionEngine` to route to correct engine
3. **Dual result collection** — checks both `Handle.IsCompleted` (runspace) and `Job.State -eq 'Completed'` (session)
4. **Update check interlacing** — periodically checks for updates during the dispatch loop
5. **Allowed functions** — built from `ServiceRegistry.FunctionEngineMap` instead of module manifests directly

```powershell
function Start-JobDispatcher {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [PSCustomObject]$Config,
        [Parameter(Mandatory)] $Receiver,
        [Parameter(Mandatory)] $Sender,
        [Parameter(Mandatory)] [Azure.Messaging.ServiceBus.ServiceBusClient]$Client,
        [Parameter(Mandatory)] [string]$JobsTopicName,
        [Parameter(Mandatory)] [PSCustomObject]$ServiceRegistry,
        # These may be $null if the corresponding engine is not enabled:
        [System.Management.Automation.Runspaces.RunspacePool]$RunspacePool,
        [PSCustomObject]$SessionPool,
        [Parameter(Mandatory)] [ref]$Running
    )

    $script:AllowedFunctions = Get-AllowedFunctions -ServiceRegistry $ServiceRegistry
    Write-WorkerLog -Message "Function whitelist: $($script:AllowedFunctions.Count) functions." -Properties @{
        AllowedFunctions = ($script:AllowedFunctions -join ', ')
    }

    Write-WorkerLog -Message 'Job dispatcher started.'
    Write-WorkerEvent -EventName 'DispatcherStarted'

    $activeJobs = [System.Collections.Generic.List[PSCustomObject]]::new()
    $lastActivityTime = [DateTime]::UtcNow
    $lastUpdateCheck = [DateTime]::UtcNow
    $idleTimeoutSeconds = $Config.IdleTimeoutSeconds

    while ($Running.Value) {
        try {
            # --- Idle timeout check (same as cloud-worker lines 236-254) ---
            if ($idleTimeoutSeconds -gt 0 -and $activeJobs.Count -eq 0) {
                $idleSeconds = ([DateTime]::UtcNow - $lastActivityTime).TotalSeconds
                if ($idleSeconds -ge $idleTimeoutSeconds) {
                    Write-WorkerLog -Message "Idle timeout reached (${idleTimeoutSeconds}s)."
                    Write-WorkerEvent -EventName 'IdleTimeoutShutdown'
                    $Running.Value = $false
                    break
                }
            }
            elseif ($activeJobs.Count -gt 0) {
                $lastActivityTime = [DateTime]::UtcNow
            }

            # --- Periodic update check ---
            if ($Config.UpdateEnabled) {
                $updateCheckInterval = $Config.UpdatePollIntervalMinutes * 60
                if (([DateTime]::UtcNow - $lastUpdateCheck).TotalSeconds -ge $updateCheckInterval) {
                    $lastUpdateCheck = [DateTime]::UtcNow
                    $updateInfo = Test-UpdateAvailable -Config $Config
                    if ($updateInfo) {
                        Write-WorkerLog -Message "Update available: v$($updateInfo.version). Initiating update..." -Severity Information
                        $downloaded = Install-WorkerUpdate -Config $Config -UpdateInfo $updateInfo
                        if ($downloaded) {
                            Write-WorkerLog -Message 'Update downloaded. Will apply after draining active jobs.'
                            $Running.Value = $false
                            # Don't break — let the shutdown drain loop handle active jobs
                        }
                    }
                }
            }

            # --- Check for completed jobs (both engines) ---
            $completedIndexes = @()
            for ($i = 0; $i -lt $activeJobs.Count; $i++) {
                $activeJob = $activeJobs[$i]
                $isCompleted = $false

                if ($activeJob.Engine -eq 'RunspacePool') {
                    $isCompleted = $activeJob.AsyncHandle.Handle.IsCompleted
                }
                elseif ($activeJob.Engine -eq 'SessionPool') {
                    $isCompleted = $activeJob.AsyncHandle.Job.State -in @('Completed', 'Failed', 'Stopped')
                }

                if ($isCompleted) {
                    $completedIndexes += $i
                    $lastActivityTime = [DateTime]::UtcNow

                    try {
                        # Collect result using the appropriate engine's collector
                        $executionResult = if ($activeJob.Engine -eq 'RunspacePool') {
                            Get-RunspaceResult -AsyncHandle $activeJob.AsyncHandle
                        } else {
                            Get-SessionResult -AsyncHandle $activeJob.AsyncHandle
                        }

                        $duration = [long]((Get-Date) - $activeJob.StartTime).TotalMilliseconds
                        $status = if ($executionResult.Success) { 'success' } else { 'failure' }

                        $resultMsg = New-JobResult -Job $activeJob.Job -WorkerId $Config.WorkerId `
                            -Status $status -Result $executionResult.Result `
                            -ErrorInfo $executionResult.Error -DurationMs $duration

                        Send-ServiceBusResult -Sender $Sender -Result $resultMsg
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $activeJob.SbMessage

                        Write-WorkerLog -Message "Job '$($activeJob.Job.JobId)' $status ($($duration)ms, $($activeJob.Engine))." -Properties @{
                            JobId = $activeJob.Job.JobId; FunctionName = $activeJob.Job.FunctionName
                            DurationMs = $duration; Engine = $activeJob.Engine
                        }
                        Write-WorkerMetric -Name 'JobDuration' -Value $duration -Properties @{
                            FunctionName = $activeJob.Job.FunctionName; Status = $status; Engine = $activeJob.Engine
                        }
                    }
                    catch {
                        Write-WorkerLog -Message "Error processing result for '$($activeJob.Job.JobId)': $($_.Exception.Message)" -Severity Error
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $activeJob.SbMessage
                    }
                }
            }

            # Remove completed (reverse order)
            for ($i = $completedIndexes.Count - 1; $i -ge 0; $i--) {
                $activeJobs.RemoveAt($completedIndexes[$i])
            }

            # --- Determine available slots per engine ---
            $runspaceActive = ($activeJobs | Where-Object { $_.Engine -eq 'RunspacePool' }).Count
            $sessionActive = ($activeJobs | Where-Object { $_.Engine -eq 'SessionPool' }).Count
            $runspaceSlots = if ($RunspacePool) { $Config.MaxParallelism - $runspaceActive } else { 0 }
            $sessionSlots = if ($SessionPool) { $SessionPool.ActiveSessions - $sessionActive } else { 0 }
            $totalSlots = $runspaceSlots + $sessionSlots

            if ($totalSlots -le 0) {
                Start-Sleep -Milliseconds 100
                continue
            }

            # --- Receive new messages ---
            $receiverRef = [ref]$Receiver
            $messages = Receive-ServiceBusMessages -ReceiverRef $receiverRef -Client $Client `
                -TopicName $JobsTopicName -WorkerId $Config.WorkerId `
                -MaxMessages $totalSlots -WaitTimeSeconds 2
            $Receiver = $receiverRef.Value

            if (-not $messages -or $messages.Count -eq 0) { continue }
            $lastActivityTime = [DateTime]::UtcNow

            foreach ($message in $messages) {
                try {
                    $job = ConvertFrom-ServiceBusMessage -Message $message
                    $validation = Test-JobMessage -Job $job
                    if (-not $validation.IsValid) {
                        # Same validation-error handling as cloud-worker lines 341-356
                        $errorResult = New-JobResult -Job ([PSCustomObject]@{
                            JobId = $job.JobId ?? 'unknown'; BatchId = $null; FunctionName = $job.FunctionName ?? 'unknown'
                        }) -WorkerId $Config.WorkerId -Status 'failure' -ErrorInfo ([PSCustomObject]@{
                            message = "Invalid job: $($validation.Error)"; type = 'ValidationError'; isThrottled = $false; attempts = 0
                        })
                        Send-ServiceBusResult -Sender $Sender -Result $errorResult
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }

                    # Check whitelist
                    if ($job.FunctionName -notin $script:AllowedFunctions) {
                        $errorResult = New-JobResult -Job $job -WorkerId $Config.WorkerId -Status 'failure' -ErrorInfo ([PSCustomObject]@{
                            message = "Function '$($job.FunctionName)' not allowed."; type = 'SecurityValidationError'; isThrottled = $false; attempts = 0
                        })
                        Send-ServiceBusResult -Sender $Sender -Result $errorResult
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }

                    # Route to correct engine
                    $engine = Get-FunctionEngine -FunctionName $job.FunctionName -ServiceRegistry $ServiceRegistry
                    $parameters = ConvertTo-ParameterHashtable -Parameters $job.Parameters

                    # Check engine-specific capacity
                    if ($engine -eq 'RunspacePool' -and $runspaceSlots -le 0) {
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }
                    if ($engine -eq 'SessionPool' -and $sessionSlots -le 0) {
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }

                    Write-WorkerLog -Message "Dispatching '$($job.JobId)': $($job.FunctionName) [$engine]" -Properties @{
                        JobId = $job.JobId; FunctionName = $job.FunctionName; Engine = $engine
                    }

                    $asyncHandle = if ($engine -eq 'RunspacePool') {
                        Invoke-InRunspace -Pool $RunspacePool -FunctionName $job.FunctionName `
                            -Parameters $parameters -MaxRetries $Config.MaxRetryCount `
                            -BaseDelaySeconds $Config.BaseRetryDelaySeconds `
                            -MaxDelaySeconds $Config.MaxRetryDelaySeconds
                    }
                    else {
                        Invoke-InSession -Pool $SessionPool -FunctionName $job.FunctionName `
                            -Parameters $parameters -MaxRetries $Config.MaxRetryCount `
                            -BaseDelaySeconds $Config.BaseRetryDelaySeconds `
                            -MaxDelaySeconds $Config.MaxRetryDelaySeconds
                    }

                    $activeJobs.Add([PSCustomObject]@{
                        Job         = $job
                        AsyncHandle = $asyncHandle
                        SbMessage   = $message
                        StartTime   = Get-Date
                        Engine      = $engine
                    })

                    # Update available slots
                    if ($engine -eq 'RunspacePool') { $runspaceSlots-- }
                    else { $sessionSlots-- }

                    Write-WorkerMetric -Name 'JobDispatched' -Value 1 -Properties @{
                        FunctionName = $job.FunctionName; Engine = $engine
                    }
                }
                catch {
                    Write-WorkerLog -Message "Error dispatching: $($_.Exception.Message)" -Severity Error
                    Abandon-ServiceBusMessage -Receiver $Receiver -Message $message
                }
            }
        }
        catch {
            Write-WorkerLog -Message "Dispatcher loop error: $($_.Exception.Message)" -Severity Error
            Write-WorkerException -Exception $_.Exception
            Start-Sleep -Seconds 1
        }
    }

    # --- Shutdown drain (reused from cloud-worker lines 417-457) ---
    if ($activeJobs.Count -gt 0) {
        Write-WorkerLog -Message "Draining $($activeJobs.Count) active job(s) ($($Config.ShutdownGraceSeconds)s grace)..."
        $timeout = [DateTime]::UtcNow.AddSeconds($Config.ShutdownGraceSeconds)

        while ($activeJobs.Count -gt 0 -and [DateTime]::UtcNow -lt $timeout) {
            $completedIndexes = @()
            for ($i = 0; $i -lt $activeJobs.Count; $i++) {
                $aj = $activeJobs[$i]
                $done = if ($aj.Engine -eq 'RunspacePool') { $aj.AsyncHandle.Handle.IsCompleted }
                        else { $aj.AsyncHandle.Job.State -in @('Completed', 'Failed', 'Stopped') }
                if ($done) {
                    $completedIndexes += $i
                    try {
                        $result = if ($aj.Engine -eq 'RunspacePool') { Get-RunspaceResult -AsyncHandle $aj.AsyncHandle }
                                  else { Get-SessionResult -AsyncHandle $aj.AsyncHandle }
                        $dur = [long]((Get-Date) - $aj.StartTime).TotalMilliseconds
                        $st = if ($result.Success) { 'success' } else { 'failure' }
                        $msg = New-JobResult -Job $aj.Job -WorkerId $Config.WorkerId -Status $st `
                            -Result $result.Result -ErrorInfo $result.Error -DurationMs $dur
                        Send-ServiceBusResult -Sender $Sender -Result $msg
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $aj.SbMessage
                    }
                    catch {
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $aj.SbMessage
                    }
                }
            }
            for ($i = $completedIndexes.Count - 1; $i -ge 0; $i--) { $activeJobs.RemoveAt($completedIndexes[$i]) }
            if ($activeJobs.Count -gt 0) { Start-Sleep -Milliseconds 200 }
        }

        if ($activeJobs.Count -gt 0) {
            Write-WorkerLog -Message "$($activeJobs.Count) job(s) did not complete within shutdown timeout." -Severity Warning
        }
    }

    Write-WorkerLog -Message 'Job dispatcher stopped.'
    Write-WorkerEvent -EventName 'DispatcherStopped'
}
```

---

## 11. Update Manager (`update-manager.ps1`) — NEW

### Blob storage layout

```
Storage Account: <storageAccountName>
  Container: hybrid-worker/
    version.json                    # Version manifest
    hybrid-worker-1.0.0.zip         # Version package
    hybrid-worker-1.1.0.zip
```

### Version manifest (`version.json`)

```json
{
  "version": "1.1.0",
  "sha256": "a1b2c3d4e5f6...",
  "fileName": "hybrid-worker-1.1.0.zip",
  "publishedAt": "2026-02-15T10:00:00Z",
  "releaseNotes": "Added Teams support"
}
```

### Functions

```powershell
function Test-UpdateAvailable {
    <#
    .SYNOPSIS
        Checks blob storage for a newer version.
    .RETURNS
        Update info object if newer version available, $null otherwise.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config
    )

    try {
        # Read current version
        $versionFile = Join-Path $Config.InstallPath 'current\version.txt'
        $currentVersion = if (Test-Path $versionFile) {
            (Get-Content $versionFile -Raw).Trim()
        } else { '0.0.0' }

        # Download version.json from blob storage
        $context = New-AzStorageContext -StorageAccountName $Config.UpdateStorageAccount -ErrorAction Stop
        $tempFile = [System.IO.Path]::GetTempFileName()

        Get-AzStorageBlobContent -Container $Config.UpdateContainerName `
            -Blob 'version.json' -Destination $tempFile `
            -Context $context -Force -ErrorAction Stop | Out-Null

        $manifest = Get-Content $tempFile -Raw | ConvertFrom-Json
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue

        # Compare versions
        $current = [Version]$currentVersion
        $available = [Version]$manifest.version

        if ($available -gt $current) {
            Write-WorkerLog -Message "Update available: $currentVersion -> $($manifest.version)" -Properties @{
                CurrentVersion   = $currentVersion
                AvailableVersion = $manifest.version
            }
            return $manifest
        }

        return $null
    }
    catch {
        Write-WorkerLog -Message "Update check failed: $($_.Exception.Message)" -Severity Warning
        return $null
    }
}

function Install-WorkerUpdate {
    <#
    .SYNOPSIS
        Downloads and stages an update package.
    .DESCRIPTION
        Downloads the zip, verifies SHA256, extracts to staging directory,
        writes an update marker file. The actual swap happens at next startup
        via Apply-PendingUpdate.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config,

        [Parameter(Mandatory)]
        [PSCustomObject]$UpdateInfo
    )

    try {
        $stagingPath = Join-Path $Config.InstallPath 'staging'
        $zipPath = Join-Path $Config.InstallPath "staging-$($UpdateInfo.version).zip"

        # Clean staging directory
        if (Test-Path $stagingPath) { Remove-Item $stagingPath -Recurse -Force }
        New-Item -Path $stagingPath -ItemType Directory -Force | Out-Null

        # Download zip from blob storage
        $context = New-AzStorageContext -StorageAccountName $Config.UpdateStorageAccount -ErrorAction Stop
        Get-AzStorageBlobContent -Container $Config.UpdateContainerName `
            -Blob $UpdateInfo.fileName -Destination $zipPath `
            -Context $context -Force -ErrorAction Stop | Out-Null

        # Verify SHA256
        $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
        if ($hash -ne $UpdateInfo.sha256) {
            Remove-Item $zipPath -Force
            throw "SHA256 mismatch: expected $($UpdateInfo.sha256), got $hash"
        }

        # Extract
        Expand-Archive -Path $zipPath -DestinationPath $stagingPath -Force
        Remove-Item $zipPath -Force

        # Write version.txt into staging
        $UpdateInfo.version | Set-Content -Path (Join-Path $stagingPath 'version.txt') -NoNewline

        # Write update marker
        @{
            version     = $UpdateInfo.version
            downloadedAt = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json | Set-Content -Path (Join-Path $Config.InstallPath 'update-pending.json')

        Write-WorkerLog -Message "Update v$($UpdateInfo.version) staged successfully."
        Write-WorkerEvent -EventName 'UpdateStaged' -Properties @{ Version = $UpdateInfo.version }
        return $true
    }
    catch {
        Write-WorkerLog -Message "Update download/staging failed: $($_.Exception.Message)" -Severity Error
        Write-WorkerException -Exception $_.Exception
        return $false
    }
}

function Apply-PendingUpdate {
    <#
    .SYNOPSIS
        Called at startup. If an update marker exists, swaps current -> previous, staging -> current.
    #>
    [CmdletBinding()]
    param(
        [string]$InstallPath = 'C:\ProgramData\MaToolkit\HybridWorker'
    )

    $markerFile = Join-Path $InstallPath 'update-pending.json'
    if (-not (Test-Path $markerFile)) { return $false }

    $marker = Get-Content $markerFile -Raw | ConvertFrom-Json
    Write-Host "[UPDATE] Applying pending update to v$($marker.version)..."

    $currentPath = Join-Path $InstallPath 'current'
    $stagingPath = Join-Path $InstallPath 'staging'
    $previousPath = Join-Path $InstallPath 'previous'

    try {
        # Remove old previous (only keep one rollback version)
        if (Test-Path $previousPath) { Remove-Item $previousPath -Recurse -Force }

        # current -> previous
        if (Test-Path $currentPath) { Rename-Item $currentPath -NewName 'previous' }

        # staging -> current
        Rename-Item $stagingPath -NewName 'current'

        # Clear marker
        Remove-Item $markerFile -Force

        Write-Host "[UPDATE] Successfully updated to v$($marker.version)."
        return $true
    }
    catch {
        Write-Host "[UPDATE] FAILED to apply update: $($_.Exception.Message)" -ForegroundColor Red
        # Attempt rollback: previous -> current
        try {
            if (-not (Test-Path $currentPath) -and (Test-Path $previousPath)) {
                Rename-Item $previousPath -NewName 'current'
                Write-Host '[UPDATE] Rolled back to previous version.' -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "[UPDATE] ROLLBACK FAILED: $($_.Exception.Message)" -ForegroundColor Red
        }
        return $false
    }
}
```

### Self-update lifecycle

The self-update flow integrates with the .NET service host's process monitoring:

1. **Worker detects update** — `Test-UpdateAvailable` finds newer version during dispatch loop
2. **Worker stages update** — `Install-WorkerUpdate` downloads zip, verifies SHA256, extracts to `staging/`, writes `update-pending.json`
3. **Worker exits cleanly** — Sets `$Running = $false`, drains active jobs, exits with code 0
4. **Service host restarts worker** — `WorkerProcessService` detects exit code 0, immediately starts a new `pwsh.exe` instance
5. **Worker applies update at boot** — `Apply-PendingUpdate` swaps `current/` -> `previous/`, `staging/` -> `current/`
6. **Worker runs new version** — Boots normally from the updated `current/` directory

Rollback: If the new version crashes repeatedly, an admin can `Stop-Service MaToolkitHybridWorker`, manually swap `previous/` -> `current/`, and `Start-Service`.

---

## 12. Health Check (`health-check.ps1`)

**Adapted from:** `src/automation/cloud-worker/src/health-check.ps1`

### Changes from cloud-worker

- Add SessionPool health check alongside RunspacePool check
- Add version info to health response
- Add update status to health response

### `Get-HealthStatus` additions

```powershell
# Add to the existing health status checks:

# SessionPool health
if ($null -ne $SessionPool) {
    $busySessions = ($SessionPool.Sessions | Where-Object { $_.Busy }).Count
    $totalSessions = $SessionPool.ActiveSessions
    $status.checks['sessionPool'] = @{
        status      = 'healthy'
        total       = $totalSessions
        busy        = $busySessions
        available   = $totalSessions - $busySessions
        utilization = [math]::Round(($busySessions / $totalSessions) * 100, 1)
    }
}

# Version info
$versionFile = Join-Path $PSScriptRoot '..\version.txt'
if (Test-Path $versionFile) {
    $status.version = (Get-Content $versionFile -Raw).Trim()
}

# Update status
$markerFile = Join-Path $Config.InstallPath 'update-pending.json'
if (Test-Path $markerFile) {
    $status.updatePending = $true
}
```

---

## 13. Worker Entry Point (`worker.ps1`)

**Adapted from:** `src/automation/cloud-worker/src/worker.ps1`

### Boot sequence (12 phases vs cloud-worker's 8)

```
Phase 1:  Apply pending update (swap staging -> current if marker exists)
Phase 2:  Load configuration (JSON file)
Phase 3:  Initialize logging (App Insights)
Phase 4:  Authenticate to Azure (SP + cert instead of managed identity)
Phase 5:  Retrieve target tenant certificate from Key Vault (conditional: if cloud services enabled)
Phase 6:  Retrieve on-prem credentials from Key Vault (conditional: if on-prem services enabled)
Phase 7:  Initialize Service Bus (ClientCertificateCredential)
Phase 8:  Initialize service connections (validate enabled services, build function map)
Phase 9:  Initialize execution engines
  Phase 9a: RunspacePool for PS 7.x functions (if cloud services enabled)
  Phase 9b: PSSession pool for PS 5.1 functions (if on-prem services enabled)
Phase 10: Start health check server
Phase 11: Register shutdown handler
Phase 12: Start job dispatcher (dual-engine routing + periodic update check)
```

### Key differences from cloud-worker

| Phase | Cloud-Worker | Hybrid-Worker |
|-------|-------------|---------------|
| 1 (update) | N/A | `Apply-PendingUpdate` — swap staged version |
| 2 (config) | `Get-WorkerConfiguration` (env vars) | `Get-WorkerConfiguration` (JSON file) |
| 3 (logging) | `cloud-worker` role name | `hybrid-worker` role name |
| 4 (Azure auth) | `Connect-AzAccount -Identity` | `Connect-HybridWorkerAzure` (SP + cert) |
| 5 (cert) | Always runs | Conditional: only if entra/exchangeOnline enabled |
| 6 (on-prem) | N/A | `Get-OnPremCredential` for each enabled on-prem service |
| 7 (SB) | `ManagedIdentityCredential` | `ClientCertificateCredential` |
| 8 (svc conn) | N/A | `Initialize-ServiceConnections` |
| 9 (engines) | `Initialize-RunspacePool` always | Conditional: RunspacePool if cloud, SessionPool if on-prem |
| 10 (health) | Same | Adds SessionPool health |
| 11 (shutdown) | SIGTERM/SIGINT | `Console.CancelKeyPress` (service host sends via process signal) |
| 12 (dispatch) | Single-engine | Dual-engine + update check |

### Shutdown handler

The .NET service host sends a termination signal to the PowerShell process via `taskkill /PID`. The worker registers `Console.CancelKeyPress` to catch this and initiate graceful shutdown:

```powershell
# Register shutdown handler for service host stop signal.
# The .NET WorkerProcessService sends a process termination signal on service stop.
# Console.CancelKeyPress catches this and sets the running flag to false, allowing
# the dispatcher to drain active jobs within the configured grace period.
try {
    [Console]::CancelKeyPress.Add({
        param($sender, $e)
        $e.Cancel = $true
        $script:WorkerRunning = $false
    }) | Out-Null
}
catch {
    Write-WorkerLog -Message 'Could not register console cancel handler.' -Severity Verbose
}

# Also register PowerShell.Exiting (same as cloud-worker)
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
    $script:WorkerRunning = $false
}
```

### Exit codes

| Code | Meaning | Service Host Behavior |
|------|---------|----------------------|
| 0 | Clean shutdown (idle timeout or update staged) | Restart immediately |
| 100 | Update applied, restart requested | Restart immediately |
| Other | Unexpected error | Wait 10s, then restart |

---

## 14. HybridFunctions Module — NEW

### Module manifest (`HybridFunctions.psd1`)

```powershell
@{
    RootModule        = 'HybridFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b2c3d4e5-f6a7-8901-bcde-f23456789012'
    Author            = 'Migration Automation Toolkit'
    Description       = 'On-premises function library for the Hybrid Worker. AD, Exchange Server, SPO, and Teams functions.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        # Active Directory
        'New-ADMigrationUser',
        'Set-ADUserAttributes',
        'Test-ADAttributeMatch',
        'Test-ADGroupMembership',
        'Add-ADGroupMember',
        'Remove-ADGroupMember',
        # Exchange Server
        'New-ExchangeRemoteMailbox',
        'Set-ExchangeRemoteMailboxAttributes',
        'Test-ExchangeRemoteMailboxMatch',
        # SharePoint Online (placeholder)
        # Teams (placeholder)
    )

    PrivateData = @{
        PSData = @{}
        ExecutionEngine   = 'SessionPool'
    }
}
```

### Module loader (`HybridFunctions.psm1`)

```powershell
$modulePath = $PSScriptRoot
$functionFiles = @(
    'ADFunctions.ps1',
    'ExchangeServerFunctions.ps1',
    'SPOFunctions.ps1',
    'TeamsFunctions.ps1'
)
foreach ($file in $functionFiles) {
    $filePath = Join-Path $modulePath $file
    if (Test-Path $filePath) { . $filePath }
}
```

### Skeleton functions

Each function follows the same return conventions as cloud-worker StandardFunctions:
- Simple action: return `$true`
- Data result: return `[PSCustomObject]` with named fields
- Validation: return `[PSCustomObject]@{ match = $true/$false; ... }`

Function bodies will be implemented based on specific migration requirements. The skeleton provides the correct parameter signatures and return shapes.

---

## 15. Installation Scripts

### `Install-HybridWorker.ps1`

```powershell
#Requires -RunAsAdministrator
#Requires -Version 7.4

<#
.SYNOPSIS
    Installs the MA Toolkit Hybrid Worker as a native Windows Service.
.DESCRIPTION
    Deploys the hybrid-worker to C:\ProgramData\MaToolkit\HybridWorker\,
    builds the .NET service host, and registers a native Windows Service
    via New-Service. Supports Group Managed Service Accounts (gMSA) and
    configures service recovery for automatic restarts on failure.
.PARAMETER ConfigPath
    Path to a pre-filled worker-config.json. If not provided, copies the
    example and prompts for edits.
.PARAMETER CertificatePath
    Path to a PFX file to import into the local machine cert store.
.PARAMETER CertificatePassword
    Password for the PFX file.
.PARAMETER ServiceAccount
    Account to run the service under. Supports:
      - gMSA: 'DOMAIN\gmsaAccount$' (recommended for production)
      - Standard: 'DOMAIN\serviceAccount' (requires -ServiceAccountPassword)
      - LocalSystem: omit this parameter (default, not recommended for production)
.PARAMETER ServiceAccountPassword
    Password for a standard service account. Not needed for gMSA.
#>
param(
    [string]$ConfigPath,
    [string]$CertificatePath,
    [SecureString]$CertificatePassword,
    [string]$ServiceAccount,
    [SecureString]$ServiceAccountPassword
)

$ErrorActionPreference = 'Stop'
$installBase = 'C:\ProgramData\MaToolkit\HybridWorker'
$serviceName = 'MaToolkitHybridWorker'
$serviceDisplayName = 'MA Toolkit Hybrid Worker'
$serviceDescription = 'Migration Automation Toolkit - Hybrid Worker Service'

# --- 1. Verify prerequisites ---
$pwshVersion = $PSVersionTable.PSVersion
if ($pwshVersion -lt [Version]'7.4') { throw "PowerShell 7.4+ required. Got: $pwshVersion" }

$winps = Get-Command powershell.exe -ErrorAction SilentlyContinue
if (-not $winps) { throw 'Windows PowerShell 5.1 (powershell.exe) not found.' }

$dotnetSdk = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetSdk) { throw '.NET SDK required to build the service host. Install from https://dot.net' }

# Verify WinRM is running (needed for PSSession pool)
$winrm = Get-Service WinRM -ErrorAction SilentlyContinue
if (-not $winrm -or $winrm.Status -ne 'Running') {
    Write-Host 'Enabling PSRemoting...'
    Enable-PSRemoting -Force -SkipNetworkProfileCheck
}

# Check for existing service
$existingService = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    throw "Service '$serviceName' already exists. Run Uninstall-HybridWorker.ps1 first or stop and update manually."
}

# --- 2. Create directory structure ---
$dirs = @('current', 'staging', 'previous', 'config', 'logs')
foreach ($dir in $dirs) {
    $path = Join-Path $installBase $dir
    if (-not (Test-Path $path)) { New-Item -Path $path -ItemType Directory -Force | Out-Null }
}

# --- 3. Copy worker files to current\ ---
$sourceDir = Split-Path -Parent $PSScriptRoot  # hybrid-worker/ root
$itemsToCopy = @('src', 'modules', 'dotnet-libs', 'version.txt')
foreach ($item in $itemsToCopy) {
    $src = Join-Path $sourceDir $item
    $dst = Join-Path $installBase "current\$item"
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $dst -Recurse -Force
    }
}

# --- 4. Build and publish the .NET service host ---
Write-Host 'Building .NET service host...'
$serviceHostProject = Join-Path $sourceDir 'service-host'
$publishOutput = Join-Path $installBase 'current\service-host'

dotnet publish $serviceHostProject `
    -c Release `
    -r win-x64 `
    --self-contained `
    -o $publishOutput `
    --nologo

if ($LASTEXITCODE -ne 0) { throw 'Failed to build .NET service host.' }
Write-Host 'Service host built successfully.'

# --- 5. Configuration ---
if ($ConfigPath -and (Test-Path $ConfigPath)) {
    Copy-Item $ConfigPath -Destination (Join-Path $installBase 'config\worker-config.json') -Force
}
elseif (-not (Test-Path (Join-Path $installBase 'config\worker-config.json'))) {
    $exampleConfig = Join-Path $sourceDir 'config\worker-config.example.json'
    Copy-Item $exampleConfig -Destination (Join-Path $installBase 'config\worker-config.json')
    Write-Host 'IMPORTANT: Edit config\worker-config.json before starting the service.' -ForegroundColor Yellow
}

# --- 6. Import certificate if provided ---
if ($CertificatePath) {
    $certFlags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet -bor
                 [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $CertificatePath, $CertificatePassword, $certFlags)
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
    $store.Open('ReadWrite')
    $store.Add($cert)
    $store.Close()
    Write-Host "Certificate imported. Thumbprint: $($cert.Thumbprint)"

    # Grant private key read access to the service account
    if ($ServiceAccount) {
        $keyName = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
        $keyPath = "C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys\$keyName"
        if (Test-Path $keyPath) {
            $acl = Get-Acl $keyPath
            $accountName = $ServiceAccount.TrimEnd('$')  # Handle gMSA trailing $
            $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
                $ServiceAccount, 'Read', 'Allow')
            $acl.AddAccessRule($rule)
            Set-Acl $keyPath $acl
            Write-Host "Granted private key access to $ServiceAccount."
        }
    }
}

# --- 7. Register Windows Service ---
$serviceHostExe = Join-Path $publishOutput 'HybridWorker.ServiceHost.exe'

$newServiceParams = @{
    Name           = $serviceName
    BinaryPathName = $serviceHostExe
    DisplayName    = $serviceDisplayName
    Description    = $serviceDescription
    StartupType    = 'Automatic'
}

# Configure service account
if ($ServiceAccount) {
    if ($ServiceAccount.EndsWith('$')) {
        # gMSA — no password needed
        $newServiceParams['Credential'] = [PSCredential]::new($ServiceAccount, (New-Object SecureString))
        Write-Host "Service will run as gMSA: $ServiceAccount"
    }
    elseif ($ServiceAccountPassword) {
        $newServiceParams['Credential'] = [PSCredential]::new($ServiceAccount, $ServiceAccountPassword)
        Write-Host "Service will run as: $ServiceAccount"
    }
    else {
        throw "ServiceAccountPassword required for non-gMSA account '$ServiceAccount'."
    }
}
else {
    Write-Host 'Service will run as LocalSystem (not recommended for production).' -ForegroundColor Yellow
}

New-Service @newServiceParams

# --- 8. Configure service recovery (restart on failure) ---
# sc.exe failure: restart after 10s, 30s, 60s; reset counter after 1 day
& sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000
& sc.exe failureflag $serviceName 1

# --- 9. Set directory permissions ---
# Restrict config directory to service account + Administrators
$configDir = Join-Path $installBase 'config'
$acl = Get-Acl $configDir
$acl.SetAccessRuleProtection($true, $false)  # Disable inheritance
$adminRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    'BUILTIN\Administrators', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($adminRule)
if ($ServiceAccount) {
    $svcRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $ServiceAccount, 'Read,ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($svcRule)
}
else {
    $systemRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        'NT AUTHORITY\SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($systemRule)
}
Set-Acl $configDir $acl

Write-Host ''
Write-Host "Service '$serviceName' installed successfully." -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. Edit configuration:  notepad $installBase\config\worker-config.json"
Write-Host "  2. Start the service:   Start-Service $serviceName"
Write-Host "  3. Check status:        Get-Service $serviceName"
Write-Host "  4. View logs:           Get-Content $installBase\logs\worker.log -Tail 50"
Write-Host "  5. Event Log:           Get-WinEvent -ProviderName $serviceName -MaxEvents 20"
```

### `Uninstall-HybridWorker.ps1`

```powershell
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Uninstalls the MA Toolkit Hybrid Worker Windows Service.
.PARAMETER RemoveFiles
    If specified, removes all worker files including configuration and logs.
#>
param(
    [switch]$RemoveFiles
)

$serviceName = 'MaToolkitHybridWorker'
$installBase = 'C:\ProgramData\MaToolkit\HybridWorker'

# Stop the service if running
$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Write-Host "Stopping service '$serviceName'..."
        Stop-Service $serviceName -Force
        # Wait for the service to fully stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    # Remove the service
    Write-Host "Removing service '$serviceName'..."
    & sc.exe delete $serviceName
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Warning: sc.exe delete returned exit code $LASTEXITCODE" -ForegroundColor Yellow
    }
}
else {
    Write-Host "Service '$serviceName' not found."
}

# Optionally remove installation directory
if ($RemoveFiles) {
    Write-Host "Removing installation directory: $installBase"
    Remove-Item $installBase -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'All worker files removed.'
}
elseif (Test-Path $installBase) {
    $confirm = Read-Host 'Remove all worker files including config and logs? (y/N)'
    if ($confirm -eq 'y') {
        Remove-Item $installBase -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'All worker files removed.'
    }
    else {
        Write-Host "Worker files preserved at: $installBase"
    }
}

Write-Host "Service '$serviceName' uninstalled." -ForegroundColor Green
```

---

## 16. Infrastructure — Bicep

### `infra/automation/hybrid-worker/deploy.bicep`

Creates Azure-side resources needed by hybrid workers. Unlike cloud-worker Bicep which creates ACA resources, this only creates Service Bus subscriptions, RBAC, and the update storage account.

```bicep
// Parameters
param baseName string
param location string = resourceGroup().location
param workerId string
param serviceBusNamespaceName string
param keyVaultName string
param servicePrincipalObjectId string  // Object ID of the hybrid worker's SP
param tags object = { component: 'hybrid-worker', project: 'ma-toolkit' }

// Optional: update storage (deploy once, shared by all hybrid workers)
param deployUpdateStorage bool = true
param updateStorageAccountName string = '${baseName}hwupdate'

// Existing resources
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = { name: serviceBusNamespaceName }
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource jobsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' existing = {
  parent: serviceBus
  name: 'worker-jobs'
}

// Service Bus subscription with SQL filter (same pattern as cloud-worker)
resource workerSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: jobsTopic
  name: 'worker-${workerId}'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    defaultMessageTimeToLive: 'P7D'
  }
}

resource workerSubscriptionRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  parent: workerSubscription
  name: 'WorkerIdFilter'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: { sqlExpression: 'WorkerId = \'${workerId}\'' }
  }
}

// RBAC: SP -> Service Bus Data Receiver
resource sbReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, servicePrincipalObjectId, '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// RBAC: SP -> Service Bus Data Sender
resource sbSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, servicePrincipalObjectId, '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// RBAC: SP -> Key Vault Secrets User
resource kvSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, servicePrincipalObjectId, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// Update storage account (shared, deploy once)
resource updateStorage 'Microsoft.Storage/storageAccounts@2023-01-01' = if (deployUpdateStorage) {
  name: updateStorageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: { allowBlobPublicAccess: false }
}

resource updateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = if (deployUpdateStorage) {
  name: '${updateStorage.name}/default/hybrid-worker'
  properties: { publicAccess: 'None' }
}

// RBAC: SP -> Storage Blob Data Reader (for update downloads)
resource storageReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployUpdateStorage) {
  scope: updateStorage
  name: guid(updateStorage.id, servicePrincipalObjectId, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output subscriptionName string = workerSubscription.name
output updateStorageAccountName string = deployUpdateStorage ? updateStorage.name : ''
```

---

## 17. CI/CD — GitHub Actions

### Addition to `.github/workflows/deploy-apps.yml`

New job: `deploy-hybrid-worker` (runs after `build-and-test` if hybrid-worker paths changed).

```yaml
deploy-hybrid-worker:
  runs-on: ubuntu-latest
  if: |
    github.event_name == 'workflow_dispatch' && contains(fromJSON('["all","hybrid-worker"]'), github.event.inputs.component) ||
    needs.build-and-test.outputs.deploy-hybrid-worker == 'true'
  needs: build-and-test
  steps:
    - name: Checkout
      uses: actions/checkout@v4

    - name: Azure Login (OIDC)
      uses: azure/login@v2
      with:
        client-id: ${{ secrets.AZURE_CLIENT_ID }}
        tenant-id: ${{ secrets.AZURE_TENANT_ID }}
        subscription-id: ${{ env.AZURE_SUBSCRIPTION_ID }}

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0.x'

    - name: Set version
      id: version
      run: |
        VERSION=$(cat src/automation/hybrid-worker/version.txt)
        echo "version=$VERSION" >> "$GITHUB_OUTPUT"
        echo "Version: $VERSION"

    - name: Build service host
      run: |
        dotnet publish src/automation/hybrid-worker/service-host/ \
          -c Release -r win-x64 --self-contained \
          -o src/automation/hybrid-worker/service-host-publish/ \
          --nologo

    - name: Package hybrid-worker
      run: |
        cd src/automation/hybrid-worker
        zip -r "hybrid-worker-${{ steps.version.outputs.version }}.zip" \
          src/ modules/ dotnet-libs/ version.txt \
          service-host-publish/ \
          -x "tests/*" "config/*" "*.md" "service-host/*.cs" "service-host/*.csproj"

    - name: Compute SHA256
      id: hash
      run: |
        HASH=$(sha256sum "src/automation/hybrid-worker/hybrid-worker-${{ steps.version.outputs.version }}.zip" | cut -d' ' -f1)
        echo "sha256=$HASH" >> "$GITHUB_OUTPUT"

    - name: Fetch update storage account name
      id: storage
      run: |
        outputs=$(az deployment group show \
          --name hybrid-worker-infra \
          --resource-group "$RESOURCE_GROUP" \
          --query properties.outputs \
          --output json)
        echo "account=$(echo "$outputs" | jq -r '.updateStorageAccountName.value')" >> "$GITHUB_OUTPUT"

    - name: Upload package to blob storage
      run: |
        az storage blob upload \
          --account-name "${{ steps.storage.outputs.account }}" \
          --container-name "hybrid-worker" \
          --name "hybrid-worker-${{ steps.version.outputs.version }}.zip" \
          --file "src/automation/hybrid-worker/hybrid-worker-${{ steps.version.outputs.version }}.zip" \
          --overwrite --auth-mode login

    - name: Update version manifest
      run: |
        cat > /tmp/version.json << EOF
        {
          "version": "${{ steps.version.outputs.version }}",
          "sha256": "${{ steps.hash.outputs.sha256 }}",
          "fileName": "hybrid-worker-${{ steps.version.outputs.version }}.zip",
          "publishedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
          "releaseNotes": "Automated build from commit ${{ github.sha }}"
        }
        EOF
        az storage blob upload \
          --account-name "${{ steps.storage.outputs.account }}" \
          --container-name "hybrid-worker" \
          --name "version.json" \
          --file "/tmp/version.json" \
          --overwrite --auth-mode login
```

### Path filter addition (in the `changes` job)

```yaml
- uses: dorny/paths-filter@v3
  id: filter
  with:
    filters: |
      hybrid-worker:
        - 'src/automation/hybrid-worker/**'
```

---

## 18. Code Reuse Summary

| File | Source | Approach |
|------|--------|----------|
| `logging.ps1` | cloud-worker `logging.ps1` | **Verbatim** (change `RoleName` constant) |
| `runspace-manager.ps1` | cloud-worker `runspace-manager.ps1` | **Verbatim** |
| `service-bus.ps1` | cloud-worker `service-bus.ps1` | **Adapted** (one credential constructor change) |
| `health-check.ps1` | cloud-worker `health-check.ps1` | **Adapted** (add SessionPool + version checks) |
| `auth.ps1` | cloud-worker `auth.ps1` | **Adapted** (SP cert instead of MI; add `Get-OnPremCredential`) |
| `config.ps1` | cloud-worker `config.ps1` | **Rewritten** (JSON file instead of env-var-only) |
| `job-dispatcher.ps1` | cloud-worker `job-dispatcher.ps1` | **Adapted** (dual-engine routing, update check) |
| `worker.ps1` | cloud-worker `worker.ps1` | **Adapted** (12-phase boot, service host integration) |
| `StandardFunctions/` | cloud-worker `modules/StandardFunctions/` | **Copied** (Entra + EXO functions) |
| `service-host/` | — | **New** (.NET 8 Worker Service) |
| `session-pool.ps1` | — | **New** |
| `service-connections.ps1` | — | **New** |
| `update-manager.ps1` | — | **New** |
| `HybridFunctions/` | — | **New** (skeleton) |
| `Install/Uninstall scripts` | — | **New** |
| `deploy.bicep` | cloud-worker `deploy.bicep` pattern | **New** (SB subscription + RBAC only, no ACA) |

---

## 19. Implementation Order

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | Directory structure, `CLAUDE.md`, `version.txt` | Scaffold project |
| 2 | `service-host/` | .NET Worker Service host (csproj, Program.cs, WorkerProcessService.cs) |
| 3 | `config.ps1` | JSON config loader with env var overrides |
| 4 | `logging.ps1` | Copy from cloud-worker, change role name |
| 5 | `auth.ps1` | SP cert auth + KV cert retrieval + on-prem credentials |
| 6 | `service-bus.ps1` | Copy from cloud-worker, swap credential type |
| 7 | `runspace-manager.ps1` | Copy from cloud-worker verbatim |
| 8 | `service-connections.ps1` | Service registry + function-to-engine mapping |
| 9 | `session-pool.ps1` | PSSession pool for PS 5.1 |
| 10 | `job-dispatcher.ps1` | Dual-engine routing + update check |
| 11 | `update-manager.ps1` | Blob storage version check + download + staging |
| 12 | `health-check.ps1` | Adapt from cloud-worker (add SessionPool health) |
| 13 | `worker.ps1` | Main entry point with 12-phase boot |
| 14 | `modules/HybridFunctions/` | Module manifest + skeleton functions |
| 15 | `modules/StandardFunctions/` | Copy from cloud-worker |
| 16 | `config/worker-config.example.json` | Example config |
| 17 | `Install-HybridWorker.ps1` | Installation script |
| 18 | `Uninstall-HybridWorker.ps1` | Removal script |
| 19 | `tests/Test-WorkerLocal.ps1` | Parse validation tests |
| 20 | `dotnet-libs/` | Copy .NET assemblies or add download script |
| 21 | `infra/automation/hybrid-worker/deploy.bicep` | Bicep template |
| 22 | `.github/workflows/deploy-apps.yml` update | CI/CD for packaging + blob upload |

---

## 20. Security Considerations

### Service identity

| Environment | Recommended Identity | Notes |
|-------------|---------------------|-------|
| Production | **gMSA** (`DOMAIN\svc-hybridworker$`) | Password auto-rotated by AD. No human knows the password. |
| Pre-production | gMSA or dedicated service account | Matches prod identity model. |
| Development | LocalSystem or developer account | Acceptable for local testing only. |

### Certificate security

- SP certificate stored in `Cert:\LocalMachine\My` with private key access restricted to the service account
- The install script automatically grants private key read access to the configured service account
- Config directory (`config\`) has inheritance disabled — only Administrators and the service account can read it

### Windows Event Log

The .NET service host writes to the Windows Event Log (`Application` log, source `MaToolkitHybridWorker`). Events include:
- Service start/stop
- Worker process crashes and restarts
- Unexpected exit codes

PowerShell-level telemetry continues to go to Application Insights (same as cloud-worker).

### Service recovery

Configured via `sc.exe failure` during installation:
- 1st failure: restart after 10 seconds
- 2nd failure: restart after 30 seconds
- 3rd+ failure: restart after 60 seconds
- Failure counter resets after 24 hours of clean operation

---

## 21. Verification Plan

1. **Parse validation:** `pwsh -File tests/Test-WorkerLocal.ps1` — all `.ps1` files parse cleanly, module manifests valid, function exports match definitions
2. **Service host build:** `dotnet build service-host/` succeeds, `dotnet publish` produces self-contained `HybridWorker.ServiceHost.exe`
3. **Config loading:** Run `worker.ps1` with a test config — verify it loads, validates, logs config summary
4. **Service Bus connectivity:** Worker connects to SB with SP cert, pre-warms token, receives test message
5. **PSSession pool:** Sessions created to localhost WinPS 5.1, modules load, `Invoke-Command { Get-ADUser -Filter 'Name -eq "test"' }` succeeds
6. **Dual-engine dispatch:** Submit one RunspacePool job and one SessionPool job — both produce results on `worker-results` topic
7. **Self-update:** Bump `version.txt`, upload new zip + `version.json` to blob storage, verify worker detects it, downloads, restarts via service host, runs new version. Verify `previous/` contains old version for rollback.
8. **Windows Service lifecycle:**
   - `Start-Service MaToolkitHybridWorker` — service starts, worker process launches
   - `Stop-Service MaToolkitHybridWorker` — graceful shutdown, active jobs drain
   - Kill `pwsh.exe` — service host detects exit, restarts worker automatically
   - `Get-WinEvent -ProviderName MaToolkitHybridWorker` — events logged correctly
9. **Health check:** `Invoke-RestMethod http://localhost:8080/health` returns JSON with both engine statuses
10. **gMSA:** Service starts and authenticates correctly when configured with a Group Managed Service Account
