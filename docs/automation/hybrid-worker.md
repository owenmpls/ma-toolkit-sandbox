# Hybrid Worker

## Overview

The hybrid worker is an on-premises PowerShell worker that runs as a Scheduled Task. A lightweight launcher checks for work every N minutes by peeking the Azure Service Bus subscription. When messages are found, it boots the full worker which executes migration functions against on-premises systems (Active Directory, Exchange Server) and cloud services (SharePoint Online, Teams) using a PS 5.1 PSSession pool. It supports multi-forest AD environments with lazy connection validation.

The hybrid worker fills a gap that the cloud worker cannot address -- on-premises infrastructure (Active Directory, Exchange Server) is not accessible from Azure Container Apps. Both workers share the same Service Bus message format, function contract, and orchestrator integration, so the orchestrator dispatches jobs to either worker type transparently based on `WorkerId`.

| Property | Value |
|----------|-------|
| Runtime | PowerShell 7.4 + Windows PowerShell 5.1 |
| Execution Model | Scheduled Task (launcher + worker) |
| Hosting | On-premises Windows Server |
| Task Name | `MaToolkitHybridWorker` |
| Role | On-prem + cloud service migration function execution |

---

## Architecture

### Scheduled Task Model

The hybrid worker uses a two-phase approach:

1. **Launcher** (`Start-HybridWorker.ps1`) -- Invoked by Task Scheduler every N minutes (default 5). Performs a lightweight "tick": applies pending updates, initializes logging and Azure auth, checks for new updates, peeks Service Bus for messages. If no messages, exits (~6-12s). If messages found, dot-sources the full worker.

2. **Worker** (`src/worker.ps1`) -- Full boot sequence: retrieves credentials, initializes Service Bus client/receiver/sender, builds session pool, starts job dispatcher. Runs until idle timeout (default 300s), then exits. Next tick picks up any new work.

**Overlap prevention**: Task Scheduler's `MultipleInstances = IgnoreNew` ensures that if the worker is still running when the next tick fires, the new instance is silently skipped.

### Single-Engine Design

The hybrid worker uses a single execution engine -- a PS 5.1 PSSession pool -- for all function execution. All supported modules (AD, Exchange Server, SPO, Teams) work within PS 5.1 PSSessions that connect to the local machine's `Microsoft.PowerShell` remoting endpoint.

| Engine | PowerShell Version | Services | Concurrency Model |
|--------|-------------------|----------|-------------------|
| PSSession Pool | PS 5.1 (powershell.exe) | Active Directory, Exchange Server, SharePoint Online, Teams | `Invoke-Command -AsJob` -- up to `maxPs51Sessions` (1-10, default 4) |

Each service connection (activeDirectory, exchangeServer, sharepointOnline, teams) can be independently enabled or disabled in the configuration. Only modules for enabled services are loaded into sessions.

### Per-Service Modules and Capability Gating

Functions are organized into per-service modules, each declaring its `RequiredService` in the module manifest's `PrivateData`. At startup, `service-connections.ps1` scans all modules and builds:

- **FunctionCatalog** -- ALL functions from ALL service modules mapped to their required service
- **AllowedFunctions** -- Only functions for ENABLED services (subset of catalog)
- **EnabledModulePaths** -- Module manifest paths to import into sessions

The job dispatcher uses three-tier validation:

1. **In AllowedFunctions** -- Dispatch normally
2. **In FunctionCatalog but NOT in AllowedFunctions** -- Return `CapabilityDisabledError`: "Function 'X' requires service 'Y' which is not enabled on this worker"
3. **Not in FunctionCatalog** -- Return `SecurityValidationError`: "Function 'X' is not a registered function"

### Multi-Forest Active Directory

The hybrid worker supports 20+ AD forests via a config-driven `forests` array under `serviceConnections.activeDirectory`. Each forest specifies a name, domain controller server, and Key Vault credential secret.

**Credential retrieval** happens at startup (PS 7.x main process):
- `Initialize-ForestManager` validates forest configs (no duplicates, required fields)
- `Get-ForestCredentials` retrieves credentials from Key Vault for each forest

**Forest connection** happens at runtime (PS 5.1 sessions):
- `$global:ForestConfigs` is injected into each session during initialization
- `Get-ADForestConnection` validates the connection on first use (lazy) and caches it
- `Reset-ADForestConnection` clears a cached connection for retry scenarios

All AD functions accept a `TargetForest` parameter and use `Get-ADForestConnection` to obtain the server and credential for that forest.

### Launcher Flow

```
Scheduled Task fires (every N minutes)
  -> Start-HybridWorker.ps1
    1. Apply pending update (file swap if staged)
    2. Load config (Get-WorkerConfiguration)
    3. Read version.txt
    4. Initialize App Insights logging
    5. Connect-AzAccount (SP + cert)
    6. Check for updates (Test-UpdateAvailable -> Install-WorkerUpdate if available)
    7. Load SB assemblies, create client + receiver, PeekMessagesAsync(1)
    8. Dispose SB resources
    9. Log LauncherTick event (version, messages found, update status)
   10. Flush telemetry
   11. If no messages -> exit 0
   12. If messages -> dot-source worker.ps1 (full boot, idle timeout, exit)
```

#### LauncherTick Event (App Insights)

```
EventName: LauncherTick
Properties:
  WorkerId        = "hybrid-worker-01"
  Version         = "1.2.3"
  MessagesFound   = 0 | N
  UpdateAvailable = true/false
  UpdateStaged    = true/false
  UpdateApplied   = true/false
  PeekFailed      = true/false
  Action          = "NoWork" | "StartingWorker"
```

### Worker Boot Sequence (11 Phases)

When the launcher invokes `worker.ps1`, phases 3 (logging) and 4 (Az auth) are skipped since the launcher already initialized them.

1. **Apply pending update** -- Atomic directory swap if `update-pending.json` exists
2. **Load configuration** -- JSON config file + environment variable overrides
3. **Initialize logging** -- Skipped if already initialized by launcher
4. **Azure authentication** -- Skipped if Az context already established by launcher
5. **Retrieve credentials** -- Forest credentials (if AD enabled) + individual service credentials (Exchange Server, SPO, Teams)
6. **Initialize Service Bus** -- Load .NET assemblies, create client/receiver/sender
7. **Initialize service connections** -- Scan per-service modules, build function catalog + whitelist
8. **Initialize SessionPool** -- PS 5.1 sessions with forest configs + per-service modules
9. **Start health check** -- Skipped when `healthCheckEnabled = false` (default)
10. **Register shutdown handler** -- `Console.CancelKeyPress` + `PowerShell.Exiting`
11. **Start job dispatcher** -- Main processing loop (blocks until idle timeout or shutdown signaled)

### Job Dispatcher Loop

Each iteration:

1. Checks idle timeout (if configured); if exceeded, signals shutdown
2. Periodically polls blob storage for updates (configurable interval, default 5 min)
3. Collects completed jobs from the SessionPool (`Job.State` checks)
4. Calculates available session slots
5. Receives new Service Bus messages (PeekLock mode, batch up to available slots)
6. For each message: validates required fields (`JobId`, `FunctionName`, `Parameters`), checks against function whitelist (with capability gating), dispatches via `Invoke-Command -AsJob`
7. On shutdown signal: drains active jobs within `ShutdownGraceSeconds`

---

## Authentication

### Azure Resources (Service Bus, Key Vault)

The hybrid worker authenticates to Azure using a service principal with a certificate stored in the local machine certificate store (`Cert:\LocalMachine\My`). This differs from the cloud worker, which uses managed identity.

- `Connect-AzAccount -ServicePrincipal -CertificateThumbprint` for the Az module
- `ClientCertificateCredential` for the Service Bus .NET SDK

### On-Premises Services (AD, Exchange Server, SPO, Teams)

Username/password stored as JSON secrets in Key Vault with the format `{"username": "domain\\user", "password": "..."}`. Retrieved at startup and converted to `PSCredential`.

- **Active Directory**: One credential per forest, each stored as a separate Key Vault secret (referenced by `credentialSecret` in the forest config)
- **Exchange Server, SPO, Teams**: One credential per service, referenced by `credentialSecret` in the service connection config

```
Hybrid Worker (hosting tenant A)
    |
    +-- SP + Certificate --> Key Vault (tenant A)
    |                            +-- Forest credential secrets (one per AD forest)
    |                            +-- Service credential secrets (Exchange, SPO, Teams)
    |
    +-- SP + Certificate --> Service Bus (tenant A)
    |
    +-- PSSession + Credential --> On-Prem AD (per forest domain controller)
    +-- PSSession + Credential --> On-Prem Exchange Server
    +-- PSSession + Credential --> SharePoint Online (via PnP/SPO module)
    +-- PSSession + Credential --> Microsoft Teams (via MicrosoftTeams module)
```

---

## Service Bus Integration

The hybrid worker uses the same `Azure.Messaging.ServiceBus` .NET SDK as the cloud worker, loaded from `dotnet-libs/` via `Add-Type`. The key difference is authentication: `ClientCertificateCredential` (service principal + certificate) instead of `ManagedIdentityCredential`.

- **Jobs topic** (`worker-jobs`): Per-worker subscription with SQL filter `WorkerId = '{workerId}'`
- **Results topic** (`worker-results`): Worker publishes results with the same message schema as the cloud worker
- Messages are received with **PeekLock** mode
- Receiver auto-recreates on transient `ServiceBusException`
- The launcher peeks on each tick to check for work before booting the full worker

### Message Format

Identical to the cloud worker. See [Cloud Worker Functions & Extensibility](cloud-worker-extensibility.md) for job and result message schemas.

---

## Self-Update Mechanism

The hybrid worker can update itself from Azure Blob Storage without manual intervention.

1. **Polling**: The launcher checks `version.json` from blob storage on each tick
2. **Staging**: Downloads the zip, verifies SHA256, extracts to `staging/`
3. **Marker**: Writes `update-pending.json` to signal the next boot
4. **Apply** (Step 1 of next launcher tick): Renames `current/ -> previous/`, `staging/ -> current/`, deletes marker
5. **Rollback**: If rename fails, restores `previous/ -> current/`

Update packages are published by the `deploy-apps.yml` GitHub Actions workflow (manual trigger only).

---

## Monitoring

The hybrid worker is monitored via App Insights `LauncherTick` events rather than a persistent health endpoint. Each tick logs version, message count, and update status. You can query App Insights for:

- **Liveness**: `LauncherTick` events arriving at the expected interval
- **Version**: `Version` property on each tick
- **Work detection**: `MessagesFound > 0` and `Action = StartingWorker`
- **Update status**: `UpdateAvailable`, `UpdateStaged`, `UpdateApplied` properties

The health check HTTP endpoint (`health-check.ps1`) is available but disabled by default (`healthCheckEnabled = false`). It can be enabled in config for environments that require an HTTP health probe.

---

## Function Library

Functions are organized into per-service modules. Each module declares its `RequiredService` in the module manifest's `PrivateData` and its `ExecutionEngine` as `SessionPool`.

### ADFunctions (RequiredService: activeDirectory)

#### Get-ADForestConnection

Returns a validated connection object for the specified forest. Uses lazy validation -- the first call validates connectivity via `Get-ADDomain`, subsequent calls return cached results.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `ForestName` | string | Yes | Name of the forest (must match a configured forest) |

**Returns:** Hashtable with `Server`, `Credential`, `ForestName`

#### Reset-ADForestConnection

Clears the cached connection for a forest, forcing re-validation on next use.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `ForestName` | string | Yes | Name of the forest to reset |

#### New-ADMigrationUser

Creates a new user in Active Directory in the specified forest.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `TargetForest` | string | Yes | Forest name (must match a configured forest) |
| `SamAccountName` | string | Yes | SAM account name |
| `UserPrincipalName` | string | Yes | UPN for the new user |
| `DisplayName` | string | Yes | Display name |
| `OrganizationalUnit` | string | Yes | OU distinguished name |
| `GivenName` | string | No | First name |
| `Surname` | string | No | Last name |
| `Description` | string | No | User description |

**Returns:** Object with `ObjectGuid`, `SamAccountName`, `UserPrincipalName`, `DistinguishedName`, `ForestName`

#### Set-ADUserAttribute

Sets a single attribute on an AD user in the specified forest.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `TargetForest` | string | Yes | Forest name |
| `Identity` | string | Yes | SAM account name, UPN, or distinguished name |
| `AttributeName` | string | Yes | Name of the attribute to set |
| `AttributeValue` | any | Yes | Value to set |

**Returns:** Object with `Identity`, `AttributeName`, `AttributeValue`, `ForestName`, `Success`

### ExchangeServerFunctions (RequiredService: exchangeServer)

#### New-ExchangeRemoteMailbox

Enables a remote mailbox for an on-premises user via Exchange Management Shell.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the AD user |
| `RemoteRoutingAddress` | string | Yes | Remote routing address (e.g., `user@contoso.mail.onmicrosoft.com`) |
| `Alias` | string | No | Mail alias |

**Returns:** Object with `Identity`, `RemoteRoutingAddress`, `PrimarySmtpAddress`

### SPOFunctions (RequiredService: sharepointOnline)

#### New-MigrationSPOSite

Creates a new SharePoint Online site for migration purposes.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Url` | string | Yes | Full URL for the new site |
| `Title` | string | Yes | Site title |
| `Owner` | string | Yes | Owner UPN |
| `Template` | string | No | Site template (default: `STS#3`) |
| `StorageQuota` | int | No | Storage quota in MB (default: `1024`) |
| `TimeZoneId` | int | No | Time zone ID (default: `10` for EST) |

**Returns:** Object with `Url`, `Title`, `Owner`, `Template`, `StorageQuota`, `Status`

### TeamsFunctions (RequiredService: teams)

#### New-MigrationTeam

Creates a new Microsoft Teams team.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `DisplayName` | string | Yes | Team display name |
| `Description` | string | No | Team description |
| `Owner` | string | Yes | Owner UPN |
| `Visibility` | string | No | `Private` (default) or `Public` |
| `Template` | string | No | Team template |

**Returns:** Object with `GroupId`, `DisplayName`, `Description`, `Owner`, `Visibility`

---

## Writing Custom Functions

Custom modules are placed in `modules/CustomFunctions/`. They follow the same function contract as the cloud worker -- see [Cloud Worker Functions & Extensibility](cloud-worker-extensibility.md) for details on parameters, return patterns, and the implementation checklist.

### Service Dependency Declaration

Custom modules must declare their required service(s) in the module manifest:

```powershell
# MyCustomModule.psd1 -- single service dependency
@{
    RootModule        = 'MyCustomModule.psm1'
    ModuleVersion     = '1.0.0'
    FunctionsToExport = @('Invoke-CustomOperation')
    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()
    PrivateData = @{
        RequiredService   = 'activeDirectory'   # One of: activeDirectory, exchangeServer, sharepointOnline, teams
        ExecutionEngine   = 'SessionPool'
    }
}
```

```powershell
# MyMultiServiceModule.psd1 -- multiple service dependencies (ALL must be enabled)
@{
    RootModule        = 'MyMultiServiceModule.psm1'
    ModuleVersion     = '1.0.0'
    FunctionsToExport = @('Invoke-CrossServiceOperation')
    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()
    PrivateData = @{
        RequiredServices  = @('activeDirectory', 'exchangeServer')   # ALL listed services must be enabled
        ExecutionEngine   = 'SessionPool'
    }
}
```

- `RequiredService` (string) -- Single service dependency; module loads if that service is enabled
- `RequiredServices` (array) -- Multiple service dependencies; module loads only if ALL listed services are enabled
- All functions run in PS 5.1 PSSessions (`ExecutionEngine = 'SessionPool'`)

### Sample Extensibility Module

The `modules/CustomFunctions/SampleCustomFunctions/` directory provides 4 documented sample functions demonstrating each service pattern:

- `Set-SampleADAttribute` -- Shows `Get-ADForestConnection` + `Set-ADUser -Server -Credential` pattern
- `Test-SampleExchangeMailbox` -- Shows Exchange Server cmdlet usage
- `Get-SampleSPOSiteInfo` -- Shows SPO cmdlet pattern
- `Get-SampleTeamInfo` -- Shows Teams cmdlet pattern

---

## Configuration

Configuration is loaded from a JSON file at `C:\ProgramData\MaToolkit\HybridWorker\config\worker-config.json`. Select fields can be overridden by environment variables.

### Configuration Reference

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `workerId` | string | Yes | -- | Unique identifier; must match Service Bus subscription filter |
| `maxPs51Sessions` | int | No | `4` | PSSession pool size (1-10) |
| `serviceBus.namespace` | string | Yes | -- | FQDN (e.g., `matoolkit-sbus.servicebus.windows.net`) |
| `serviceBus.jobsTopicName` | string | No | `worker-jobs` | Inbound jobs topic |
| `serviceBus.resultsTopicName` | string | No | `worker-results` | Outbound results topic |
| `auth.tenantId` | string | Yes | -- | Hosting tenant ID (where SP and KV live) |
| `auth.appId` | string | Yes | -- | Service principal client ID |
| `auth.certificateThumbprint` | string | Yes | -- | Thumbprint of cert in `Cert:\LocalMachine\My` |
| `auth.keyVaultName` | string | Yes | -- | Key Vault name for secrets |
| `serviceConnections.activeDirectory.enabled` | bool | No | `false` | Enable AD functions |
| `serviceConnections.activeDirectory.forests` | array | Conditional | -- | Forest config array (required if AD enabled) |
| `serviceConnections.activeDirectory.forests[].name` | string | Yes | -- | Forest name (e.g., `corp.contoso.com`) |
| `serviceConnections.activeDirectory.forests[].server` | string | Yes | -- | Domain controller FQDN |
| `serviceConnections.activeDirectory.forests[].credentialSecret` | string | Yes | -- | KV secret name for forest credentials |
| `serviceConnections.exchangeServer.enabled` | bool | No | `false` | Enable Exchange Server functions |
| `serviceConnections.exchangeServer.connectionUri` | string | Conditional | -- | Exchange PowerShell URI (required if enabled) |
| `serviceConnections.exchangeServer.credentialSecret` | string | Conditional | -- | KV secret name for Exchange service account |
| `serviceConnections.sharepointOnline.enabled` | bool | No | `false` | Enable SPO functions |
| `serviceConnections.sharepointOnline.adminUrl` | string | Conditional | -- | SPO admin center URL (required if enabled) |
| `serviceConnections.sharepointOnline.credentialSecret` | string | Conditional | -- | KV secret name for SPO service account |
| `serviceConnections.teams.enabled` | bool | No | `false` | Enable Teams functions |
| `serviceConnections.teams.credentialSecret` | string | Conditional | -- | KV secret name for Teams service account |
| `appInsights.connectionString` | string | No | -- | Application Insights connection string |
| `update.enabled` | bool | No | `true` | Enable self-update polling |
| `update.storageAccountName` | string | Conditional | -- | Storage account for update packages (required if updates enabled) |
| `update.containerName` | string | No | `hybrid-worker` | Blob container name |
| `update.pollIntervalMinutes` | int | No | `5` | Update check frequency |
| `idleTimeoutSeconds` | int | No | `0` | Exit after N seconds idle (recommended: 300 for scheduled task mode) |
| `shutdownGraceSeconds` | int | No | `30` | Job drain timeout on shutdown |
| `healthCheckEnabled` | bool | No | `false` | Enable HTTP health endpoint (disabled by default) |
| `healthCheckPort` | int | No | `8080` | HTTP health endpoint port (only used if healthCheckEnabled) |
| `scheduledTaskIntervalMinutes` | int | No | `5` | Reference value for task interval (used by installer) |
| `logPath` | string | No | `C:\ProgramData\MaToolkit\HybridWorker\logs` | Log file output directory |

### Environment Variable Overrides

| Variable | Overrides |
|----------|-----------|
| `HYBRID_WORKER_ID` | `workerId` |
| `HYBRID_WORKER_MAX_PS51_SESSIONS` | `maxPs51Sessions` |
| `HYBRID_WORKER_SB_NAMESPACE` | `serviceBus.namespace` |
| `HYBRID_WORKER_JOBS_TOPIC` | `serviceBus.jobsTopicName` |
| `HYBRID_WORKER_RESULTS_TOPIC` | `serviceBus.resultsTopicName` |

### Example Configuration

```json
{
  "workerId": "hybrid-worker-01",
  "maxPs51Sessions": 4,

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

  "serviceConnections": {
    "activeDirectory": {
      "enabled": true,
      "forests": [
        {
          "name": "corp.contoso.com",
          "server": "dc01.corp.contoso.com",
          "credentialSecret": "ad-cred-contoso"
        },
        {
          "name": "emea.contoso.com",
          "server": "dc01.emea.contoso.com",
          "credentialSecret": "ad-cred-emea"
        }
      ]
    },
    "exchangeServer": {
      "enabled": true,
      "connectionUri": "http://exchange01.corp.contoso.com/PowerShell/",
      "credentialSecret": "exchange-service-account"
    },
    "sharepointOnline": {
      "enabled": false,
      "adminUrl": "https://contoso-admin.sharepoint.com",
      "credentialSecret": "spo-service-account"
    },
    "teams": {
      "enabled": false,
      "credentialSecret": "teams-service-account"
    }
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

  "idleTimeoutSeconds": 300,
  "shutdownGraceSeconds": 30,
  "healthCheckEnabled": false,
  "healthCheckPort": 8080,
  "scheduledTaskIntervalMinutes": 5,
  "logPath": "C:\\ProgramData\\MaToolkit\\HybridWorker\\logs"
}
```

---

## Differences from Cloud Worker

| Aspect | Cloud Worker | Hybrid Worker |
|--------|-------------|---------------|
| Hosting | Azure Container Apps | Scheduled Task on-premises |
| Auth to Azure | ManagedIdentityCredential | ClientCertificateCredential (SP + cert in local machine cert store) |
| Scale model | KEDA scale-to-zero | Launcher peeks SB, full boot only when work found |
| Execution engine | PS 7.x RunspacePool | PS 5.1 PSSession pool (single engine) |
| Services | Entra ID, Exchange Online | Active Directory, Exchange Server, SharePoint Online, Teams |
| Module structure | StandardFunctions (monolithic) | Per-service modules with capability gating |
| Multi-forest AD | N/A | Config-driven forests array with lazy connection validation |
| Configuration | Environment variables | JSON config file + env var overrides |
| Update mechanism | Container image rebuild + ACA update | Blob storage version check + staged directory swap |
| Monitoring | Container health probes | App Insights LauncherTick events + Task Scheduler history |
| Deployment | `az acr build` + `az containerapp update` | `Register-ScheduledTask` (no .NET SDK needed) |

---

## Operations

### Task Management

```powershell
# Check task status
Get-ScheduledTask -TaskName MaToolkitHybridWorker

# Run a tick manually
Start-ScheduledTask -TaskName MaToolkitHybridWorker

# Disable the task (stop future ticks)
Disable-ScheduledTask -TaskName MaToolkitHybridWorker

# Enable the task
Enable-ScheduledTask -TaskName MaToolkitHybridWorker

# Stop a running worker
Stop-ScheduledTask -TaskName MaToolkitHybridWorker

# View task history
Get-ScheduledTask -TaskName MaToolkitHybridWorker | Get-ScheduledTaskInfo

# View Task Scheduler GUI
taskschd.msc
```

### Updating Configuration

1. Disable the task: `Disable-ScheduledTask -TaskName MaToolkitHybridWorker`
2. Edit `C:\ProgramData\MaToolkit\HybridWorker\config\worker-config.json`
3. Enable the task: `Enable-ScheduledTask -TaskName MaToolkitHybridWorker`

### Uninstalling

```powershell
# Stop + remove task, prompt for file removal
.\Uninstall-HybridWorkerTask.ps1

# Stop + remove task + all files
.\Uninstall-HybridWorkerTask.ps1 -RemoveFiles
```

---

## Project File Layout

```
hybrid-worker/
+-- Start-HybridWorker.ps1                 # Launcher (invoked by Scheduled Task)
+-- Install-HybridWorkerTask.ps1           # First-time setup (run as admin)
+-- Uninstall-HybridWorkerTask.ps1         # Clean removal
+-- Download-Dependencies.ps1              # Fetch NuGet packages for dotnet-libs/
+-- version.txt                            # Current version
+-- src/
|   +-- worker.ps1                         # Full worker (11-phase boot)
|   +-- config.ps1                         # JSON config loader + env var overrides
|   +-- logging.ps1                        # App Insights telemetry
|   +-- auth.ps1                           # SP cert auth + credential retrieval
|   +-- service-bus.ps1                    # SB integration (ClientCertificateCredential)
|   +-- ad-forest-manager.ps1              # Multi-forest config validation
|   +-- session-pool.ps1                   # PS 5.1 PSSession pool (single engine)
|   +-- job-dispatcher.ps1                 # Capability-gated dispatch + update check
|   +-- service-connections.ps1            # Per-service module scanning + catalog
|   +-- update-manager.ps1                 # Blob storage version check + zip download
|   +-- health-check.ps1                   # Health endpoint (disabled by default)
+-- modules/
|   +-- ADFunctions/                       # RequiredService = activeDirectory
|   +-- ExchangeServerFunctions/           # RequiredService = exchangeServer
|   +-- SPOFunctions/                      # RequiredService = sharepointOnline
|   +-- TeamsFunctions/                    # RequiredService = teams
|   +-- CustomFunctions/                   # Customer-specific extensibility modules
+-- config/
|   +-- worker-config.example.json         # Example configuration file
+-- tests/
|   +-- Test-WorkerLocal.ps1               # Parse + structure + stale reference validation
+-- dotnet-libs/                           # .NET assemblies (fetched by Download-Dependencies.ps1)
```

---

## Troubleshooting

### Task fires but worker doesn't start

Check Task Scheduler history for errors:

```powershell
Get-ScheduledTask -TaskName MaToolkitHybridWorker | Get-ScheduledTaskInfo
```

Common causes:
- PowerShell 7.4+ not installed or not in PATH
- Configuration file missing or malformed JSON
- Certificate thumbprint does not match any certificate in `Cert:\LocalMachine\My`
- Task account lacks read access to the private key

### Service Bus authentication fails

- Verify the service principal has `Service Bus Data Receiver` and `Service Bus Data Sender` roles on the namespace
- Verify the certificate thumbprint in config matches the imported certificate
- Check that `disableLocalAuth` is `true` on the Service Bus namespace (SP uses RBAC, not SAS)

### PSSession pool fails to initialize

- Verify WinRM is running: `Get-Service WinRM`
- Verify PSRemoting is enabled: `Test-WSMan localhost`
- Check that the task account has remote access to the PS 5.1 endpoint
- For AD functions: verify the domain controller is reachable and the credential secret is correct

### Key Vault access denied

- Verify the service principal has `Key Vault Secrets User` role on the Key Vault
- Check that Key Vault firewall allows access from the worker's IP address (or add the IP to the allow list)

### LauncherTick events not appearing in App Insights

- Verify `appInsights.connectionString` is set in the configuration
- Check that `dotnet-libs/Microsoft.ApplicationInsights.dll` exists
- Verify network connectivity to App Insights ingestion endpoint
