# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This is the **Migration Automation (M&A) Toolkit sandbox** — a collection of experimental components for Microsoft 365 tenant-to-tenant migration automation. The codebase is exploratory and largely vibe-coded.

## Projects

### ps-cloud-worker (`src/automation/ps-cloud-worker/`)

The primary component: a containerized PowerShell 7.4+ worker that runs in Azure Container Apps. It processes migration jobs (Entra ID + Exchange Online operations) via Azure Service Bus using a RunspacePool for parallel execution with per-runspace authenticated sessions.

See `src/automation/ps-cloud-worker/CLAUDE.md` for detailed project context, architecture decisions, and implementation notes.

## Common Commands

All commands should be run from the `src/automation/ps-cloud-worker/` directory.

### Run local validation tests (no Azure credentials required)

```bash
pwsh -File tests/Test-WorkerLocal.ps1
```

This validates parse correctness for all .ps1 files, module manifest, function exports, and function definitions (18 tests).

### Build the container image

```bash
docker build -t matoolkitacr.azurecr.io/ps-cloud-worker:latest .
```

### Run locally with Docker Compose

```bash
docker-compose up
```

### Deploy infrastructure (Azure)

```bash
az deployment group create \
  --resource-group your-rg \
  --template-file ../../../infrastructure/automation/ps-cloud-worker/deploy.bicep \
  --parameters ../../../infrastructure/automation/ps-cloud-worker/deploy.parameters.json
```

### Submit test jobs (requires Azure credentials + running worker)

```powershell
./tests/Submit-TestJob.ps1 `
  -CsvPath ./tests/sample-jobs.csv `
  -ServiceBusNamespace 'matoolkit-sb.servicebus.windows.net' `
  -WorkerId 'worker-01' `
  -FunctionName 'New-EntraUser'
```

## Architecture

The worker follows a **queue-based, scale-to-zero pattern**:

1. **Azure Service Bus** topics (`jobs` / `results`) connect the orchestrator to the worker
2. **KEDA scaler** on the worker's subscription triggers ACA to scale 0→1 when messages arrive
3. **Worker boot** (8 phases): config → logging → Azure auth → Key Vault secret → Service Bus client → RunspacePool with per-runspace Graph+EXO sessions → shutdown handler → job dispatch loop
4. **Job dispatch loop**: receive (PeekLock) → validate → dispatch to available runspace → collect result → send result message → complete/abandon original message
5. **Idle timeout** (default 300s): worker exits gracefully so ACA scales back to zero

Key design choices:
- **RunspacePool** (not ThreadJobs or `-Parallel`) — each runspace maintains isolated MgGraph + EXO connections to avoid thread-safety issues
- **Service Bus .NET SDK** loaded directly — `Az.ServiceBus` is management-plane only
- **EXO auth workaround**: client secret → OAuth token via REST → `Connect-ExchangeOnline -AccessToken` (EXO app-only natively requires certs)
- **Throttle handling**: exponential backoff + jitter inside each runspace, respects `Retry-After` headers

## PowerShell Gotchas

These are real bugs encountered during development:
- `$Error` is a read-only automatic variable — use `$ErrorInfo` as parameter names
- `"Runspace $Index:"` causes parse errors due to `:` after variable — use `${Index}:` syntax
- `Connect-ExchangeOnline -ShowBanner:$false` fails in scriptblocks — use splatting instead
