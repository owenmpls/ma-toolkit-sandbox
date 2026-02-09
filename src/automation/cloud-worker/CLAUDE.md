# Claude Context — Cloud Worker

## What This Project Is

A containerized PowerShell worker running in Azure Container Apps, part of the Migration Automation Toolkit's **automation subsystem**. It executes migration functions against a target Microsoft 365 tenant (Entra ID via MgGraph and Exchange Online) on behalf of an orchestrator that communicates via Azure Service Bus.

Refer to the architecture sketch titled "Automation subsystem architecture" for the full system context. This project implements the "PowerShell worker (container) in Azure Container Apps" component.

## Project Structure

```
cloud-worker/
├── Dockerfile, .dockerignore, docker-compose.yml
├── CLAUDE.md                              ← this file
├── src/
│   ├── worker.ps1                         # Main entry point (8-phase boot sequence)
│   ├── config.ps1                         # Env var loader + validation
│   ├── auth.ps1                           # Managed identity, AKV cert retrieval, MgGraph + EXO cert auth
│   ├── servicebus.ps1                     # .NET SDK for send/receive (Azure.Messaging.ServiceBus)
│   ├── runspace-manager.ps1               # RunspacePool creation, per-runspace auth, async dispatch
│   ├── job-dispatcher.ps1                 # Main loop: receive → validate → dispatch → collect → send results
│   └── logging.ps1                        # App Insights TelemetryClient + console fallback
├── modules/
│   ├── StandardFunctions/
│   │   ├── StandardFunctions.psd1/psm1    # Module manifest + loader
│   │   ├── EntraFunctions.ps1             # 8 functions: user, group, B2B, validation
│   │   └── ExchangeFunctions.ps1          # 6 functions: mail user, validation
│   └── CustomFunctions/
│       └── ExampleCustomModule/           # Sample custom function module
├── tests/
│   ├── Submit-TestJob.ps1                 # CSV-driven test job submitter
│   ├── sample-jobs.csv
│   └── Test-WorkerLocal.ps1              # Parse + structure validation (20 tests)
└── (subsystem docs at docs/automation/)
```

## Key Architecture Decisions

- **Parallelism**: RunspacePool (not ThreadJobs, not ForEach-Object -Parallel). Each runspace gets its own MgGraph + EXO session to avoid thread-safety issues with shared connections.
- **Service Bus SDK**: Azure.Messaging.ServiceBus .NET assembly loaded directly in PowerShell (Az.ServiceBus is management-plane only).
- **Certificate auth**: Certificate (PFX) stored in Key Vault, retrieved at startup. Both MgGraph and EXO use native certificate-based auth (`Connect-MgGraph -Certificate`, `Connect-ExchangeOnline -Certificate`). PFX bytes are passed to runspaces (byte arrays serialize cleanly across runspace boundaries) and reconstructed with `EphemeralKeySet` flag (avoids writing private keys to disk on Linux).
- **Scale-to-zero**: ACA configured min=0 / max=1 with KEDA `azure-servicebus` scaler monitoring the worker's subscription. Worker has idle timeout (default 300s) — when no messages and no active jobs for that duration, it exits gracefully so ACA scales to zero.
- **Throttle handling**: Retry with exponential backoff + jitter happens *inside* each runspace. Throttling exceptions are not reported as failures unless retries are exhausted.
- **Module system**: Standard functions ship with the container. Custom functions are discovered from `CustomFunctions/` directory (volume mount or baked into image).

## 14 Standard Library Functions

**Entra (EntraFunctions.ps1):**
- `New-EntraUser` → Object
- `Set-EntraUserUPN` → Boolean
- `Add-EntraGroupMember` → Boolean
- `Remove-EntraGroupMember` → Boolean
- `New-EntraB2BInvitation` — invites an *existing internal user* to B2B collaboration (uses `New-MgInvitation` with `InvitedUser.Id`)
- `Convert-EntraB2BToInternal` — converts external→internal via beta API (`POST /beta/users/{id}/convertExternalToInternalMemberUser`)
- `Test-EntraAttributeMatch` → Object (supports multi-value)
- `Test-EntraGroupMembership` → Object

**Exchange (ExchangeFunctions.ps1):**
- `Add-ExchangeSecondaryEmail` → Boolean
- `Set-ExchangePrimaryEmail` → Boolean
- `Set-ExchangeExternalAddress` → Boolean
- `Set-ExchangeMailUserGuids` → Boolean
- `Test-ExchangeAttributeMatch` → Object (supports multi-value)
- `Test-ExchangeGroupMembership` → Object

## Service Bus Message Format

**Job message** (worker-jobs topic, filtered by `WorkerId` application property):
```json
{
    "JobId": "guid",
    "BatchId": "optional-batch-id",
    "WorkerId": "worker-01",
    "FunctionName": "New-EntraUser",
    "Parameters": { "DisplayName": "...", ... }
}
```

**Result message** (worker-results topic):
```json
{
    "JobId": "guid", "BatchId": "...", "WorkerId": "worker-01",
    "FunctionName": "New-EntraUser",
    "Status": "Success|Failure",
    "ResultType": "Boolean|Object",
    "Result": true | { ... },
    "Error": null | { "Message": "...", "Type": "...", "IsThrottled": false, "Attempts": 1 },
    "DurationMs": 1234,
    "Timestamp": "ISO8601"
}
```

## Important Implementation Notes

- The `$Error` automatic variable is read-only in PowerShell. We use `$ErrorInfo` as the parameter name in `New-JobResult` and callers pass `-ErrorInfo`.
- String interpolation with `:` after a variable name (e.g., `"Runspace $Index:"`) causes parse errors. Use `${Index}:` syntax.
- EXO `Connect-ExchangeOnline -ShowBanner:$false` causes parse issues in scriptblocks. Use splatting instead.
- The `New-RandomPassword` utility function lives in `EntraFunctions.ps1` (not exported, used internally by `New-EntraUser` and `Convert-EntraB2BToInternal`).

## Tests

Run `pwsh -File tests/Test-WorkerLocal.ps1` — validates parse correctness for all .ps1 files, module structure, manifest exports (14 functions), function definitions, and certificate auth migration (removed functions, no client_secret references). Currently 20/20 passing.

## What's Not Built Yet

This is the worker component only. The broader automation subsystem includes:
- **Orchestrator** (Azure Functions) — manages batch state, dispatches runbook execution, sends jobs, receives results
- **State machine DB** (Azure SQL) — runbook state, step progression, rollback
- **Runbook definitions** (YAML in blob storage) — tailorable migration runbooks
- **Engagement subsystem** — Dataverse + Power Automate for user communications
- **Analytics subsystem** — Databricks SQL for remediation/prep data
- **On-premises worker** — PowerShell in Docker for AD/Exchange on-prem operations
