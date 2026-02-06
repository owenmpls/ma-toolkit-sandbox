# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This is the **Migration Automation (M&A) Toolkit sandbox** — a collection of experimental components for Microsoft 365 tenant-to-tenant migration automation. The codebase is exploratory and largely vibe-coded.

## Projects

### cloud-worker (`src/automation/cloud-worker/`)

A containerized PowerShell 7.4+ worker that runs in Azure Container Apps. It processes migration jobs (Entra ID + Exchange Online operations) via Azure Service Bus using a RunspacePool for parallel execution with per-runspace authenticated sessions.

See `src/automation/cloud-worker/CLAUDE.md` for detailed project context, architecture decisions, and implementation notes.

### scheduler (`src/automation/scheduler/`)

C# Azure Functions project (isolated worker, .NET 8). The timing and detection engine for the migration pipeline. Runs on a 5-minute timer, reads YAML runbook definitions from SQL, queries external data sources to discover migration members, detects batches, evaluates phase timing, and dispatches events to the orchestrator via Azure Service Bus.

See `src/automation/scheduler/CLAUDE.md` for details.

### admin-api (`src/automation/admin-api/`)

C# Azure Functions project (isolated worker, .NET 8). Comprehensive admin API for the migration pipeline. Combines runbook management with batch creation, CSV uploads, and automation control.

| Method | Route | Description |
|--------|-------|-------------|
| **Runbook Management** | | |
| POST | `/api/runbooks` | Publish new runbook version |
| GET | `/api/runbooks` | List all active runbooks |
| GET | `/api/runbooks/{name}` | Get latest active version |
| GET | `/api/runbooks/{name}/versions` | List all versions |
| GET | `/api/runbooks/{name}/versions/{v}` | Get specific version |
| DELETE | `/api/runbooks/{name}/versions/{v}` | Deactivate version |
| **Automation Control** | | |
| GET | `/api/runbooks/{name}/automation` | Get automation status |
| PUT | `/api/runbooks/{name}/automation` | Enable/disable automation |
| **Query & Templates** | | |
| POST | `/api/runbooks/{name}/query/preview` | Preview query results |
| GET | `/api/runbooks/{name}/template` | Download CSV template |
| **Batch Management** | | |
| GET | `/api/batches` | List batches with filters |
| POST | `/api/batches` | Create batch from CSV |
| GET | `/api/batches/{id}` | Get batch details |
| POST | `/api/batches/{id}/advance` | Advance manual batch |
| POST | `/api/batches/{id}/cancel` | Cancel batch |
| **Member Management** | | |
| GET | `/api/batches/{id}/members` | List batch members |
| POST | `/api/batches/{id}/members` | Add members from CSV |
| DELETE | `/api/batches/{id}/members/{memberId}` | Remove member |
| **Execution Tracking** | | |
| GET | `/api/batches/{id}/phases` | List phase executions |
| GET | `/api/batches/{id}/steps` | List step executions |

See `src/automation/admin-api/CLAUDE.md` for details.

### orchestrator (`src/automation/orchestrator/`)

C# Azure Functions project (isolated worker, .NET 8). Consumes events from the scheduler via Service Bus and coordinates step execution by dispatching jobs to cloud-workers.

See `src/automation/orchestrator/CLAUDE.md` for details.

## Common Commands

### Build .NET projects

```bash
# Build all .NET projects
dotnet build src/automation/scheduler/
dotnet build src/automation/admin-api/
dotnet build src/automation/orchestrator/

# Run Functions locally (requires Azure Functions Core Tools v4)
cd src/automation/admin-api/src/AdminApi.Functions && func start
```

### cloud-worker commands

Run from `src/automation/cloud-worker/`:

```bash
# Run local validation tests (no Azure credentials required)
pwsh -File tests/Test-WorkerLocal.ps1

# Build the container image
docker build -t matoolkitacr.azurecr.io/cloud-worker:latest .

# Run locally with Docker Compose
docker-compose up
```

### Deploy infrastructure (Azure)

```bash
# Deploy admin-api
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/admin-api/deploy.bicep \
  --parameters infra/automation/admin-api/deploy.parameters.json \
  --parameters sqlConnectionString="your-connection-string"

# Deploy cloud-worker
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/cloud-worker/deploy.bicep \
  --parameters infra/automation/cloud-worker/deploy.parameters.json
```

## Architecture

The system follows an **event-driven, queue-based pattern**:

1. **Admin API** → Manages runbooks, enables/disables automation, supports manual batch creation via CSV upload
2. **Scheduler** → Timer-triggered, queries data sources (when automation enabled), detects batches, dispatches events to Service Bus
3. **Orchestrator** → Consumes scheduler events, coordinates step execution, dispatches jobs to workers
4. **Cloud Worker** → Executes migration operations (Entra ID, Exchange Online) via RunspacePool

### Cloud Worker Details

The worker follows a **scale-to-zero pattern**:
- **KEDA scaler** triggers ACA to scale 0→1 when Service Bus messages arrive
- **RunspacePool** with per-runspace Graph + EXO sessions (isolated connections avoid thread-safety issues)
- **Idle timeout** (300s): worker exits gracefully so ACA scales back to zero

Key design choices:
- Service Bus .NET SDK loaded directly (`Az.ServiceBus` is management-plane only)
- EXO auth workaround: client secret → OAuth token via REST → `Connect-ExchangeOnline -AccessToken`
- Throttle handling: exponential backoff + jitter, respects `Retry-After` headers

## PowerShell Gotchas

These are real bugs encountered during development:
- `$Error` is a read-only automatic variable — use `$ErrorInfo` as parameter names
- `"Runspace $Index:"` causes parse errors due to `:` after variable — use `${Index}:` syntax
- `Connect-ExchangeOnline -ShowBanner:$false` fails in scriptblocks — use splatting instead
