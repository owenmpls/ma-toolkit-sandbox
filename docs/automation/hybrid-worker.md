# Hybrid Worker

## Overview

The hybrid worker is an on-premises PowerShell worker running as a native Windows Service. It receives job messages from Azure Service Bus and executes migration functions against both cloud services (Entra ID, Exchange Online) and on-premises systems (Active Directory, Exchange Server). It uses a dual-engine architecture: PS 7.x RunspacePool for cloud functions and PS 5.1 PSSession pool for on-prem functions.

The hybrid worker fills a gap that the cloud worker cannot address -- on-premises infrastructure (Active Directory, Exchange Server) is not accessible from Azure Container Apps. Both workers share the same Service Bus message format, function contract, and orchestrator integration, so the orchestrator dispatches jobs to either worker type transparently based on `WorkerId`.

| Property | Value |
|----------|-------|
| Runtime | PowerShell 7.4 + Windows PowerShell 5.1 |
| Service Host | .NET 8 Worker Service (Windows Service) |
| Hosting | On-premises Windows Server |
| Service Name | `MaToolkitHybridWorker` |
| Role | Dual-engine migration function execution (cloud + on-prem) |

---

## Architecture

### Dual-Engine Design

The hybrid worker runs two execution engines side by side:

| Engine | PowerShell Version | Services | Concurrency Model |
|--------|-------------------|----------|-------------------|
| RunspacePool | PS 7.x (pwsh.exe) | Entra ID, Exchange Online | `RunspacePool` -- up to `maxParallelism` (1-20, default 2) |
| PSSession Pool | PS 5.1 (powershell.exe) | Active Directory, Exchange Server | `Invoke-Command -AsJob` -- up to `maxPs51Sessions` (1-10, default 2) |

The dual-engine design exists because on-premises AD and Exchange Server cmdlets are Windows PowerShell 5.1 only (they do not run in PS 7.x), while cloud SDK modules (MgGraph, EXO) work exclusively in PS 7.x. The PSSession pool connects to the local machine's PS 5.1 remoting endpoint (`localhost`, `Microsoft.PowerShell` configuration) to access on-prem cmdlets.

Each service connection (entra, exchangeOnline, activeDirectory, exchangeServer, sharepointOnline, teams) can be independently enabled or disabled in the configuration. Only enabled engines are initialized at startup.

### Process Architecture

The .NET 8 Worker Service (`HybridWorker.ServiceHost.exe`) runs as the Windows Service and manages the `pwsh.exe` process lifecycle:

1. Launches `pwsh.exe -NoProfile -NonInteractive -File worker.ps1`
2. Sets `HYBRID_WORKER_CONFIG_PATH` and `HYBRID_WORKER_INSTALL_PATH` environment variables
3. Pipes stdout/stderr to `logs/worker.log`
4. On service stop: sends `taskkill /PID /T` to signal the process tree, waits up to `ShutdownGraceSeconds` (default 45) for clean exit
5. On unexpected crash (exit code other than 0 or 100): waits 10 seconds and restarts
6. Logs to Windows Event Log as source `MaToolkitHybridWorker`

### Boot Sequence (12 Phases)

1. **Apply pending update** -- Atomic directory swap if `update-pending.json` exists
2. **Load configuration** -- JSON config file + environment variable overrides
3. **Initialize logging** -- App Insights TelemetryClient with console fallback
4. **Azure authentication** -- Service principal + certificate in `Cert:\LocalMachine\My`
5. **Retrieve target-tenant certificate** -- PFX from Key Vault (only if cloud services enabled)
6. **Retrieve on-prem credentials** -- Username/password from Key Vault secrets (only for enabled on-prem services)
7. **Initialize Service Bus** -- Load .NET assemblies, create client/receiver/sender
8. **Initialize service connection registry** -- Build function-to-engine mapping from module manifests
9a. **Initialize RunspacePool** -- PS 7.x, if cloud services enabled
9b. **Initialize SessionPool** -- PS 5.1, if on-prem services enabled
10. **Start health check** -- Background HTTP server on configurable port
11. **Register shutdown handler** -- `Console.CancelKeyPress` + `PowerShell.Exiting`
12. **Start job dispatcher** -- Main processing loop (blocks until shutdown signaled)

### Job Dispatcher Loop

Each iteration:

1. Checks idle timeout (if configured); if exceeded, signals shutdown
2. Periodically polls blob storage for updates (configurable interval, default 5 min)
3. Collects completed jobs from both engines (`Handle.IsCompleted` for RunspacePool, `Job.State` for SessionPool)
4. Calculates available slots per engine
5. Receives new Service Bus messages (PeekLock mode, batch up to available slots)
6. For each message: validates required fields (`JobId`, `FunctionName`, `Parameters`), checks against function whitelist, routes to the correct engine, dispatches asynchronously
7. On shutdown signal: drains active jobs within `ShutdownGraceSeconds`

### Function Routing

The `service-connections.ps1` module reads module manifests at startup to build a `FunctionEngineMap` (function name -> `RunspacePool` or `SessionPool`). The routing is determined by the `PrivateData.ExecutionEngine` field in each module's `.psd1` manifest:

- `RunspacePool` -- Function runs in PS 7.x RunspacePool (cloud modules)
- `SessionPool` -- Function runs in PS 5.1 PSSession pool (on-prem modules)

Unknown functions are rejected with a `SecurityValidationError` without execution.

---

## Authentication

### Azure Resources (Service Bus, Key Vault)

The hybrid worker authenticates to Azure using a service principal with a certificate stored in the local machine certificate store (`Cert:\LocalMachine\My`). This differs from the cloud worker, which uses managed identity.

- `Connect-AzAccount -ServicePrincipal -CertificateThumbprint` for the Az module
- `ClientCertificateCredential` for the Service Bus .NET SDK

### Target Tenant Cloud Services (MgGraph, EXO)

PFX certificate retrieved from Key Vault at startup, exported as `[byte[]]`, and passed to each runspace. Byte arrays serialize cleanly across runspace boundaries; `X509Certificate2` private key handles do not. Each runspace reconstructs the certificate with `EphemeralKeySet`.

- `Connect-MgGraph -Certificate` for Microsoft Graph
- `Connect-ExchangeOnline -Certificate` for Exchange Online (via splatting)

### On-Premises Services (AD, Exchange Server)

Username/password stored as JSON secrets in Key Vault with the format `{"username": "domain\\user", "password": "..."}`. Retrieved at startup and converted to `PSCredential` for the PSSession pool initialization.

```
Hybrid Worker (hosting tenant A)
    |
    +-- SP + Certificate --> Key Vault (tenant A)
    |                            +-- Target tenant PFX certificate
    |                            +-- On-prem credential secrets
    |
    +-- SP + Certificate --> Service Bus (tenant A)
    |
    +-- App Registration --> Target Tenant (tenant B)
    |   +-- Graph API      (Connect-MgGraph -Certificate)
    |   +-- Exchange Online (Connect-ExchangeOnline -Certificate)
    |
    +-- PSSession + Credential --> On-Prem AD (domain controller)
    +-- PSSession + Credential --> On-Prem Exchange Server
```

---

## Service Bus Integration

The hybrid worker uses the same `Azure.Messaging.ServiceBus` .NET SDK as the cloud worker, loaded from `dotnet-libs/` via `Add-Type`. The key difference is authentication: `ClientCertificateCredential` (service principal + certificate) instead of `ManagedIdentityCredential`.

- **Jobs topic** (`worker-jobs`): Per-worker subscription with SQL filter `WorkerId = '{workerId}'`
- **Results topic** (`worker-results`): Worker publishes results with the same message schema as the cloud worker
- Messages are received with **PeekLock** mode
- Receiver auto-recreates on transient `ServiceBusException`
- Pre-warms by peeking on startup to surface auth failures early

### Message Format

Identical to the cloud worker. See [Cloud Worker Functions & Extensibility](cloud-worker-extensibility.md) for job and result message schemas.

---

## Self-Update Mechanism

The hybrid worker can update itself from Azure Blob Storage without manual intervention.

1. **Polling**: Downloads `version.json` from blob storage and compares `[Version]` with `current/version.txt`
2. **Staging**: Downloads the zip, verifies SHA256, extracts to `staging/`
3. **Marker**: Writes `update-pending.json` to signal the next boot
4. **Graceful exit**: Sets `Running = $false` to drain active jobs, then exits with code 100
5. **Restart**: Service host detects the exit and restarts `pwsh.exe`
6. **Apply** (Phase 1 of boot): Renames `current/ -> previous/`, `staging/ -> current/`, deletes marker
7. **Rollback**: If rename fails, restores `previous/ -> current/`

Update packages are published by the `deploy-apps.yml` GitHub Actions workflow (manual trigger only).

---

## Health Check

Runs as a background `Start-Job` listening on a configurable port (default 8080) using `System.Net.HttpListener`. Returns JSON with:

- `workerRunning` state
- `runspacePool` state, available slots, utilization %
- `sessionPool` total/busy/available sessions, utilization %
- `serviceBus` receiver connection status
- Worker metadata (ID, max parallelism, idle timeout)
- Version and `updatePending` flag
- HTTP 503 if unhealthy

---

## Standard Function Library (Cloud)

The hybrid worker includes the same `StandardFunctions` module as the cloud worker, providing 14 functions for Entra ID and Exchange Online operations. These run in the PS 7.x RunspacePool engine.

See [Cloud Worker Functions & Extensibility](cloud-worker-extensibility.md) for the full function reference.

---

## Hybrid Function Library (On-Prem)

The `HybridFunctions` module provides 9 functions for on-premises operations. These run in the PS 5.1 PSSession pool engine.

### Active Directory Functions (ADFunctions.ps1)

#### New-ADMigrationUser

Creates a new user in Active Directory.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `SamAccountName` | string | Yes | SAM account name |
| `UserPrincipalName` | string | Yes | UPN for the new user |
| `DisplayName` | string | Yes | Display name |
| `OrganizationalUnit` | string | Yes | OU distinguished name |
| `GivenName` | string | No | First name |
| `Surname` | string | No | Last name |
| `Description` | string | No | User description |

**Returns:** Object with `ObjectGuid`, `SamAccountName`, `UserPrincipalName`, `DistinguishedName`

#### Set-ADUserAttributes

Sets attributes on an AD user using the `-Replace` parameter.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | SAM account name, UPN, or distinguished name |
| `Attributes` | hashtable | Yes | Attribute name-value pairs to set |

**Returns:** Boolean

#### Test-ADAttributeMatch

Checks if an AD user attribute matches an expected value.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | SAM account name, UPN, or distinguished name |
| `AttributeName` | string | Yes | Name of the attribute to check |
| `ExpectedValue` | any | Yes | Expected value to match against |

**Returns:** Object with `match` (bool), `expected`, `actual`

#### Test-ADGroupMembership

Checks if a user is a member of an AD group.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `UserIdentity` | string | Yes | SAM account name of the user |
| `GroupIdentity` | string | Yes | Group name or distinguished name |

**Returns:** Object with `isMember` (bool), `group`, `user`

#### Add-ADGroupMember

Adds a user to an AD group.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `GroupIdentity` | string | Yes | Group name or distinguished name |
| `MemberIdentity` | string | Yes | SAM account name of the user to add |

**Returns:** Boolean

#### Remove-ADGroupMember

Removes a user from an AD group.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `GroupIdentity` | string | Yes | Group name or distinguished name |
| `MemberIdentity` | string | Yes | SAM account name of the user to remove |

**Returns:** Boolean

### Exchange Server Functions (ExchangeServerFunctions.ps1)

#### New-ExchangeRemoteMailbox

Enables a remote mailbox for an on-premises user.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the AD user |
| `RemoteRoutingAddress` | string | Yes | Remote routing address (e.g., `user@contoso.mail.onmicrosoft.com`) |
| `Alias` | string | No | Mail alias |

**Returns:** Object with `Identity`, `RemoteRoutingAddress`, `PrimarySmtpAddress`

#### Set-ExchangeRemoteMailboxAttributes

Sets attributes on a remote mailbox.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the remote mailbox |
| `Attributes` | hashtable | No | Attribute name-value pairs to set |

**Returns:** Boolean

#### Test-ExchangeRemoteMailboxMatch

Checks if a remote mailbox attribute matches an expected value. Handles multi-value collections (e.g., email addresses).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the remote mailbox |
| `AttributeName` | string | Yes | Name of the attribute to check |
| `ExpectedValue` | any | Yes | Expected value to match against |

**Returns:** Object with `match` (bool), `expected`, `actual`

---

## Writing Custom Functions

Custom modules are placed in `modules/CustomFunctions/`. They follow the same function contract as the cloud worker -- see [Cloud Worker Functions & Extensibility](cloud-worker-extensibility.md) for details on parameters, return patterns, and the implementation checklist.

### Execution Engine Declaration

Custom modules must declare their execution engine in the module manifest:

```powershell
# MyCustomModule.psd1
@{
    RootModule        = 'MyCustomModule.psm1'
    ModuleVersion     = '1.0.0'
    FunctionsToExport = @('Invoke-CustomOperation')
    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()
    PrivateData = @{
        ExecutionEngine = 'SessionPool'   # or 'RunspacePool'
    }
}
```

- `RunspacePool` -- Function runs in PS 7.x with MgGraph and EXO sessions pre-authenticated
- `SessionPool` -- Function runs in PS 5.1 PSSessions with access to AD and Exchange Server cmdlets

If `ExecutionEngine` is not specified, the module defaults to `RunspacePool`.

---

## Configuration

Configuration is loaded from a JSON file at `C:\ProgramData\MaToolkit\HybridWorker\config\worker-config.json`. Select fields can be overridden by environment variables.

### Configuration Reference

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `workerId` | string | Yes | -- | Unique identifier; must match Service Bus subscription filter |
| `maxParallelism` | int | No | `2` | RunspacePool max concurrent cloud jobs (1-20) |
| `maxPs51Sessions` | int | No | `2` | PSSession pool size for on-prem jobs (1-10) |
| `serviceBus.namespace` | string | Yes | -- | FQDN (e.g., `matoolkit-sbus.servicebus.windows.net`) |
| `serviceBus.jobsTopicName` | string | No | `worker-jobs` | Inbound jobs topic |
| `serviceBus.resultsTopicName` | string | No | `worker-results` | Outbound results topic |
| `auth.tenantId` | string | Yes | -- | Hosting tenant ID (where SP and KV live) |
| `auth.appId` | string | Yes | -- | Service principal client ID |
| `auth.certificateThumbprint` | string | Yes | -- | Thumbprint of cert in `Cert:\LocalMachine\My` |
| `auth.keyVaultName` | string | Yes | -- | Key Vault name for secrets and certificates |
| `targetTenant.tenantId` | string | Conditional | -- | Target M365 tenant ID (required if cloud services enabled) |
| `targetTenant.organization` | string | Conditional | -- | `*.onmicrosoft.com` domain for EXO (required if cloud services enabled) |
| `targetTenant.appId` | string | Conditional | -- | SP client ID in target tenant (required if cloud services enabled) |
| `targetTenant.certificateName` | string | No | `worker-app-cert` | KV certificate name for target tenant auth |
| `serviceConnections.entra.enabled` | bool | No | `false` | Enable Entra ID functions (RunspacePool) |
| `serviceConnections.exchangeOnline.enabled` | bool | No | `false` | Enable Exchange Online functions (RunspacePool) |
| `serviceConnections.activeDirectory.enabled` | bool | No | `false` | Enable AD functions (SessionPool) |
| `serviceConnections.activeDirectory.domainController` | string | Conditional | -- | Domain controller FQDN (required if AD enabled) |
| `serviceConnections.activeDirectory.credentialSecret` | string | Conditional | -- | KV secret name for AD service account creds |
| `serviceConnections.exchangeServer.enabled` | bool | No | `false` | Enable Exchange Server functions (SessionPool) |
| `serviceConnections.exchangeServer.connectionUri` | string | Conditional | -- | Exchange PowerShell URI (required if Exchange Server enabled) |
| `serviceConnections.exchangeServer.credentialSecret` | string | Conditional | -- | KV secret name for Exchange service account creds |
| `serviceConnections.sharepointOnline.enabled` | bool | No | `false` | SPO functions (placeholder) |
| `serviceConnections.teams.enabled` | bool | No | `false` | Teams functions (placeholder) |
| `appInsights.connectionString` | string | No | -- | Application Insights connection string |
| `update.enabled` | bool | No | `true` | Enable self-update polling |
| `update.storageAccountName` | string | Conditional | -- | Storage account for update packages (required if updates enabled) |
| `update.containerName` | string | No | `hybrid-worker` | Blob container name |
| `update.pollIntervalMinutes` | int | No | `5` | Update check frequency |
| `idleTimeoutSeconds` | int | No | `0` | Exit after N seconds idle (0 = run persistently) |
| `shutdownGraceSeconds` | int | No | `30` | Job drain timeout on shutdown |
| `healthCheckPort` | int | No | `8080` | HTTP health endpoint port |
| `logPath` | string | No | `C:\ProgramData\MaToolkit\HybridWorker\logs` | Log file output directory |

### Environment Variable Overrides

| Variable | Overrides |
|----------|-----------|
| `HYBRID_WORKER_ID` | `workerId` |
| `HYBRID_WORKER_MAX_PARALLELISM` | `maxParallelism` |
| `HYBRID_WORKER_MAX_PS51_SESSIONS` | `maxPs51Sessions` |
| `HYBRID_WORKER_SB_NAMESPACE` | `serviceBus.namespace` |
| `HYBRID_WORKER_JOBS_TOPIC` | `serviceBus.jobsTopicName` |
| `HYBRID_WORKER_RESULTS_TOPIC` | `serviceBus.resultsTopicName` |

### Example Configuration

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
    "sharepointOnline": { "enabled": false },
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

---

## Differences from Cloud Worker

| Aspect | Cloud Worker | Hybrid Worker |
|--------|-------------|---------------|
| Hosting | Azure Container Apps | Windows Service on-premises |
| Auth to Azure | ManagedIdentityCredential | ClientCertificateCredential (SP + cert in local machine cert store) |
| Scale model | KEDA scale-to-zero | Persistent service (`idleTimeoutSeconds = 0` by default) |
| Execution engines | Single: PS 7.x RunspacePool | Dual: PS 7.x RunspacePool + PS 5.1 PSSession pool |
| Module support | StandardFunctions only | StandardFunctions (cloud) + HybridFunctions (on-prem) + CustomFunctions |
| Configuration | Environment variables | JSON config file + env var overrides |
| Update mechanism | Container image rebuild + ACA update | Blob storage version check + staged directory swap |
| Deployment | `az acr build` + `az containerapp update` | Manual install + service host auto-restarts on update |

---

## Operations

### Service Management

```powershell
# Start the service
Start-Service MaToolkitHybridWorker

# Stop the service
Stop-Service MaToolkitHybridWorker

# Check status
Get-Service MaToolkitHybridWorker

# View recent logs
Get-Content C:\ProgramData\MaToolkit\HybridWorker\logs\worker.log -Tail 50

# View Windows Event Log entries
Get-WinEvent -ProviderName MaToolkitHybridWorker -MaxEvents 20
```

### Health Check

```powershell
Invoke-RestMethod http://localhost:8080/health
```

### Updating Configuration

1. Stop the service: `Stop-Service MaToolkitHybridWorker`
2. Edit `C:\ProgramData\MaToolkit\HybridWorker\config\worker-config.json`
3. Start the service: `Start-Service MaToolkitHybridWorker`

### Uninstalling

```powershell
# Stop + remove service, prompt for file removal
.\Uninstall-HybridWorker.ps1

# Stop + remove service + all files
.\Uninstall-HybridWorker.ps1 -RemoveFiles
```

---

## Project File Layout

```
hybrid-worker/
+-- Install-HybridWorker.ps1              # First-time setup (run as admin)
+-- Uninstall-HybridWorker.ps1            # Clean removal
+-- Download-Dependencies.ps1             # Fetch NuGet packages for dotnet-libs/
+-- version.txt                           # Current version
+-- service-host/                         # .NET 8 Worker Service (Windows Service host)
|   +-- HybridWorker.ServiceHost.csproj
|   +-- Program.cs
|   +-- WorkerProcessService.cs
|   +-- appsettings.json
+-- src/
|   +-- worker.ps1                        # Main entry point (12-phase boot)
|   +-- config.ps1                        # JSON config loader + env var overrides
|   +-- logging.ps1                       # App Insights telemetry
|   +-- auth.ps1                          # SP cert auth + on-prem credential retrieval
|   +-- service-bus.ps1                   # SB integration (ClientCertificateCredential)
|   +-- runspace-manager.ps1              # PS 7.x RunspacePool
|   +-- session-pool.ps1                  # PS 5.1 PSSession pool
|   +-- job-dispatcher.ps1               # Dual-engine routing + update check
|   +-- service-connections.ps1           # Service registry + function-to-engine mapping
|   +-- update-manager.ps1               # Blob storage version check + zip download
|   +-- health-check.ps1                 # Health endpoint
+-- modules/
|   +-- StandardFunctions/               # Cloud functions (PS 7.x, same as cloud-worker)
|   +-- HybridFunctions/                 # On-prem functions (PS 5.1 via PSSession)
|   +-- CustomFunctions/                 # Customer-specific modules
+-- config/
|   +-- worker-config.example.json       # Example configuration file
+-- tests/
|   +-- Test-WorkerLocal.ps1             # Parse + structure validation tests
+-- dotnet-libs/                         # .NET assemblies (fetched by Download-Dependencies.ps1)
```

---

## Troubleshooting

### Service fails to start

Check the Windows Event Log for startup errors:

```powershell
Get-WinEvent -ProviderName MaToolkitHybridWorker -MaxEvents 10
```

Common causes:
- PowerShell 7.4+ not installed or not in PATH
- Configuration file missing or malformed JSON
- Certificate thumbprint does not match any certificate in `Cert:\LocalMachine\My`
- Service account lacks read access to the private key

### Service Bus authentication fails

- Verify the service principal has `Service Bus Data Receiver` and `Service Bus Data Sender` roles on the namespace
- Verify the certificate thumbprint in config matches the imported certificate
- Check that `disableLocalAuth` is `true` on the Service Bus namespace (SP uses RBAC, not SAS)

### PSSession pool fails to initialize

- Verify WinRM is running: `Get-Service WinRM`
- Verify PSRemoting is enabled: `Test-WSMan localhost`
- Check that the service account has remote access to the PS 5.1 endpoint
- For AD functions: verify the domain controller is reachable and the credential secret is correct

### Key Vault access denied

- Verify the service principal has `Key Vault Secrets User` role on the Key Vault
- Check that Key Vault firewall allows access from the worker's IP address (or add the IP to the allow list)
