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

C# Azure Functions project (isolated worker, .NET 8). Comprehensive admin API for the migration pipeline. Combines runbook management with batch creation, CSV uploads, and automation control. All endpoints are secured with Entra ID JWT bearer authentication (`Microsoft.Identity.Web`) — write operations require the `Admin` app role, read operations require any authenticated user.

| Method | Route | Description | Auth |
|--------|-------|-------------|------|
| **Runbook Management** | | | |
| POST | `/api/runbooks` | Publish new runbook version | Admin |
| GET | `/api/runbooks` | List all active runbooks | Authenticated |
| GET | `/api/runbooks/{name}` | Get latest active version | Authenticated |
| GET | `/api/runbooks/{name}/versions` | List all versions | Authenticated |
| GET | `/api/runbooks/{name}/versions/{v}` | Get specific version | Authenticated |
| DELETE | `/api/runbooks/{name}/versions/{v}` | Deactivate version | Admin |
| **Automation Control** | | | |
| GET | `/api/runbooks/{name}/automation` | Get automation status | Authenticated |
| PUT | `/api/runbooks/{name}/automation` | Enable/disable automation | Admin |
| **Query & Templates** | | | |
| POST | `/api/runbooks/{name}/query/preview` | Preview query results | Admin |
| GET | `/api/runbooks/{name}/template` | Download CSV template | Authenticated |
| **Batch Management** | | | |
| GET | `/api/batches` | List batches with filters | Authenticated |
| POST | `/api/batches` | Create batch from CSV | Admin |
| GET | `/api/batches/{id}` | Get batch details | Authenticated |
| POST | `/api/batches/{id}/advance` | Advance manual batch | Admin |
| POST | `/api/batches/{id}/cancel` | Cancel batch | Admin |
| **Member Management** | | | |
| GET | `/api/batches/{id}/members` | List batch members | Authenticated |
| POST | `/api/batches/{id}/members` | Add members from CSV | Admin |
| DELETE | `/api/batches/{id}/members/{memberId}` | Remove member | Admin |
| **Execution Tracking** | | | |
| GET | `/api/batches/{id}/phases` | List phase executions | Authenticated |
| GET | `/api/batches/{id}/steps` | List step executions | Authenticated |

See `src/automation/admin-api/CLAUDE.md` for project context. Additional docs:
- [`docs/architecture.md`](src/automation/admin-api/docs/architecture.md) — Auth flow, request processing layers, DI tree, data model, key flows
- [`docs/usage-guide.md`](src/automation/admin-api/docs/usage-guide.md) — Full API reference with request/response examples, common workflows

### orchestrator (`src/automation/orchestrator/`)

C# Azure Functions project (isolated worker, .NET 8). Consumes events from the scheduler via Service Bus and coordinates step execution by dispatching jobs to cloud-workers.

See `src/automation/orchestrator/CLAUDE.md` for details.

### admin-cli (`src/automation/admin-cli/`)

Cross-platform .NET CLI tool (`matoolkit`) for managing M&A Toolkit automation. Provides full coverage of the Admin API with Entra ID device code flow authentication.

```bash
# Install as global tool
cd src/automation/admin-cli/src/AdminCli && dotnet pack
dotnet tool install --global --add-source ./bin/Release MaToolkit.AdminCli

# Configure and authenticate
matoolkit config set api-url https://your-api.azurewebsites.net
matoolkit config set tenant-id YOUR_TENANT_ID
matoolkit config set client-id YOUR_CLIENT_ID
matoolkit auth login

# Example usage
matoolkit runbook list
matoolkit batch create my-runbook members.csv
matoolkit batch advance 123
```

Key commands: `runbook`, `automation`, `query`, `template`, `batch`, `auth`, `config`

See `src/automation/admin-cli/CLAUDE.md` for project context. Additional docs:
- [`docs/architecture.md`](src/automation/admin-cli/docs/architecture.md) — Component diagram, auth token caching, config system, testing strategy
- [`docs/usage-guide.md`](src/automation/admin-cli/docs/usage-guide.md) — Installation, setup, full command reference, workflows, troubleshooting

## Common Commands

### Build .NET projects

```bash
# Build all .NET projects
dotnet build src/automation/scheduler/
dotnet build src/automation/admin-api/
dotnet build src/automation/orchestrator/
dotnet build src/automation/admin-cli/

# Run Functions locally (requires Azure Functions Core Tools v4)
cd src/automation/admin-api/src/AdminApi.Functions && func start

# Run CLI
dotnet run --project src/automation/admin-cli/src/AdminCli/ -- --help
```

### Run tests

```bash
# Run all tests
dotnet test src/automation/shared/MaToolkit.Automation.Shared.Tests/
dotnet test src/automation/admin-api/tests/AdminApi.Functions.Tests/
dotnet test src/automation/admin-cli/tests/AdminCli.Tests/

# Run with verbose output
dotnet test src/automation/admin-cli/tests/AdminCli.Tests/ --verbosity normal
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

### Infrastructure layout (`infra/`)

```
infra/
├── shared/                              ← shared across all subsystems
│   ├── deploy.bicep                     ← Log Analytics, Service Bus, Key Vault, ACR
│   ├── deploy.parameters.json
│   ├── network.bicep                    ← VNet, subnets, private endpoints, DNS zones
│   └── network.parameters.json
└── automation/                          ← per-component templates
    ├── scheduler-orchestrator/
    │   ├── deploy.bicep                 ← SQL, storage, Function Apps (scheduler + orchestrator)
    │   └── deploy.parameters.json
    ├── admin-api/
    │   ├── deploy.bicep                 ← storage, Function App
    │   └── deploy.parameters.json
    └── cloud-worker/
        ├── deploy.bicep                 ← ACA environment + app
        └── deploy.parameters.json
```

### Deploy infrastructure (Azure)

Deploy in four stages. The network template must come after component templates (it references their storage accounts) and the components must be re-deployed with subnet IDs to enable VNet integration.

```bash
# 1. Deploy shared infrastructure (Service Bus, Key Vault, Log Analytics, ACR)
az deployment group create \
  --resource-group your-rg \
  --template-file infra/shared/deploy.bicep \
  --parameters infra/shared/deploy.parameters.json

# 2. Deploy components (creates SQL, storage accounts, Function Apps, ACA)
# These can run in parallel with each other:

# 2a. Scheduler + orchestrator
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/scheduler-orchestrator/deploy.bicep \
  --parameters infra/automation/scheduler-orchestrator/deploy.parameters.json \
  --parameters sqlAdminPassword="your-password" \
  --parameters schedulerSubnetId="" orchestratorSubnetId=""

# 2b. Admin API
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/admin-api/deploy.bicep \
  --parameters infra/automation/admin-api/deploy.parameters.json \
  --parameters sqlConnectionString="your-connection-string" \
  --parameters entraIdTenantId="your-tenant-id" \
  --parameters entraIdClientId="your-client-id" \
  --parameters entraIdAudience="api://your-client-id" \
  --parameters adminApiSubnetId=""

# 2c. Cloud worker
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/cloud-worker/deploy.bicep \
  --parameters infra/automation/cloud-worker/deploy.parameters.json \
  --parameters cloudWorkerSubnetId=""

# 3. Deploy network (VNet, subnets, private endpoints, DNS zones)
# Must run after step 2 — references storage accounts created by component templates
az deployment group create \
  --resource-group your-rg \
  --template-file infra/shared/network.bicep \
  --parameters infra/shared/network.parameters.json

# 4. Re-deploy components with subnet IDs to enable VNet integration
# Use the same commands as step 2, but with subnet IDs populated in parameter files
# (the parameter files already contain the correct subnet references)

# 4a. Scheduler + orchestrator (with VNet)
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/scheduler-orchestrator/deploy.bicep \
  --parameters infra/automation/scheduler-orchestrator/deploy.parameters.json \
  --parameters sqlAdminPassword="your-password"

# 4b. Admin API (with VNet)
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/admin-api/deploy.bicep \
  --parameters infra/automation/admin-api/deploy.parameters.json \
  --parameters sqlConnectionString="your-connection-string" \
  --parameters entraIdTenantId="your-tenant-id" \
  --parameters entraIdClientId="your-client-id" \
  --parameters entraIdAudience="api://your-client-id"

# 4c. Cloud worker (with VNet — internal CAE)
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

## Code Review Recommendations

- **2026-02-06**: Full codebase review of `src/automation/` and `infra/` — [`docs/code-review-2026-02-06.md`](docs/code-review-2026-02-06.md). Covers 27 issues (4 critical, 10 high, 13 medium/low) with specific file paths, line numbers, and fix descriptions. An implementation plan exists at `.claude/plans/deep-coalescing-mitten.md`.
- **2026-02-07**: Pre-deployment review of `src/automation/` and `infra/` — [`docs/code-review-2026-02-07.md`](docs/code-review-2026-02-07.md). Covers 28 issues across 5 tiers (4 schema drift, 7 infrastructure, 3 cross-service contracts, 9 application-level, 5 deferred) organized by deployment-lock-in risk.

## PowerShell Gotchas

These are real bugs encountered during development:
- `$Error` is a read-only automatic variable — use `$ErrorInfo` as parameter names
- `"Runspace $Index:"` causes parse errors due to `:` after variable — use `${Index}:` syntax
- `Connect-ExchangeOnline -ShowBanner:$false` fails in scriptblocks — use splatting instead
