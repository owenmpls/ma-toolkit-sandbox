# CLAUDE.md -- Orchestrator

## Project Overview

C# Azure Functions project using the **isolated worker model** (.NET 8, Functions v4). The orchestrator is the job dispatch and result processing engine for the M&A Toolkit migration pipeline. It receives events from the scheduler via Service Bus, dispatches jobs to cloud workers, processes results, manages step progression, and handles rollback sequences.

## Build and Run

```bash
# Build
dotnet build src/automation/orchestrator/src/Orchestrator.Functions/

# Run locally (requires Azure Functions Core Tools v4)
cd src/automation/orchestrator/src/Orchestrator.Functions && func start

# Publish (release build)
dotnet publish src/automation/orchestrator/src/Orchestrator.Functions/ -c Release -o src/automation/orchestrator/src/Orchestrator.Functions/publish
```

## Directory Structure

```
orchestrator/
  Orchestrator.sln
  CLAUDE.md
  docs/
    architecture.md              # System architecture and data flow
  src/
    Orchestrator.Functions/
      Orchestrator.Functions.csproj
      Program.cs                 # DI registration, ServiceBusClient, all services
      host.json                  # Functions host config, App Insights sampling
      local.settings.json        # Local dev config (connection strings)
      Settings/
        OrchestratorSettings.cs  # Options class: SqlConnectionString, ServiceBusNamespace, topic names
      Functions/
        OrchestratorEventFunction.cs  # ServiceBusTrigger: orchestrator-events subscription
        WorkerResultFunction.cs       # ServiceBusTrigger: worker-results subscription
      Models/
        Db/
          RunbookRecord.cs            # runbooks table
          BatchRecord.cs              # batches table
          BatchMemberRecord.cs        # batch_members table
          PhaseExecutionRecord.cs     # phase_executions table
          StepExecutionRecord.cs      # step_executions table
          InitExecutionRecord.cs      # init_executions table
        Yaml/
          RunbookDefinition.cs        # Top-level YAML model (copied from scheduler)
          DataSourceConfig.cs         # data_source section
          PhaseDefinition.cs          # Phase with name, offset, steps
          StepDefinition.cs           # Step with name, worker_id, function, params, on_failure, poll
          PollConfig.cs               # Poll interval and timeout
          RollbackSequence.cs         # Named rollback sequence
          MultiValuedColumnConfig.cs  # Column name + format
        Messages/
          BatchInitMessage.cs         # batch-init event (from scheduler)
          PhaseDueMessage.cs          # phase-due event (from scheduler)
          MemberAddedMessage.cs       # member-added event (from scheduler)
          MemberRemovedMessage.cs     # member-removed event (from scheduler)
          PollCheckMessage.cs         # poll-check event (from scheduler)
          WorkerJobMessage.cs         # Job dispatch to worker-jobs topic
          WorkerResultMessage.cs      # Result from worker-results topic
      Services/
        DbConnectionFactory.cs        # Singleton: creates SqlConnection from config
        RunbookParser.cs              # YamlDotNet deserialization
        TemplateResolver.cs           # Resolves {{ColumnName}} templates from member data
        PhaseEvaluator.cs             # Parses offsets, calculates due_at
        DynamicTableReader.cs         # Reads member data from runbook dynamic tables
        WorkerDispatcher.cs           # Sends jobs to worker-jobs Service Bus topic
        RollbackExecutor.cs           # Executes rollback sequences on failure
        Repositories/
          RunbookRepository.cs        # Read runbook YAML for rollbacks
          BatchRepository.cs          # Status updates (active, completed, failed)
          MemberRepository.cs         # Read member data
          PhaseExecutionRepository.cs # Status updates, completion tracking
          StepExecutionRepository.cs  # Full lifecycle: dispatched, succeeded, failed, polling
          InitExecutionRepository.cs  # Full lifecycle for init steps
        Handlers/
          BatchInitHandler.cs         # Process batch-init: dispatch init steps sequentially
          PhaseDueHandler.cs          # Process phase-due: dispatch steps by index
          MemberAddedHandler.cs       # Process member-added: create catch-up steps
          MemberRemovedHandler.cs     # Process member-removed: cancel pending, run cleanup
          PollCheckHandler.cs         # Process poll-check: timeout check, re-dispatch
          ResultProcessor.cs          # Process worker results: update status, advance progression
```

## NuGet Packages

| Package | Version | Purpose |
|---|---|---|
| Microsoft.Azure.Functions.Worker | 2.0.0 | Isolated worker model runtime |
| Microsoft.Azure.Functions.Worker.Sdk | 2.0.0 | Build tooling for isolated worker |
| Microsoft.Azure.Functions.Worker.Extensions.ServiceBus | 5.22.0 | Service Bus trigger binding |
| Microsoft.Extensions.Hosting | 9.0.0 | Generic host |
| Azure.Messaging.ServiceBus | 7.18.2 | Service Bus SDK for publishing messages |
| Azure.Identity | 1.13.1 | DefaultAzureCredential for managed identity auth |
| Microsoft.Data.SqlClient | 6.0.1 | SQL Server connectivity |
| Dapper | 2.1.35 | Lightweight ORM for all SQL operations |
| YamlDotNet | 16.2.1 | YAML deserialization for runbook definitions |
| Microsoft.ApplicationInsights.WorkerService | 2.22.0 | Application Insights telemetry |

## Key Architecture Decisions

### Message Flow

1. **Scheduler → Orchestrator** via `orchestrator-events` topic:
   - `batch-init`: New batch ready for init step execution
   - `phase-due`: Phase is due, dispatch step executions
   - `member-added`: New member joined active batch
   - `member-removed`: Member left batch
   - `poll-check`: Time to re-poll a polling step

2. **Orchestrator → Worker** via `worker-jobs` topic:
   - `WorkerJobMessage`: Job ID, worker pool, function, parameters, correlation data

3. **Worker → Orchestrator** via `worker-results` topic:
   - `WorkerResultMessage`: Job ID, status, result/error, correlation data

### Step Execution Model

- **Init steps**: Execute sequentially (one at a time) within a batch
- **Phase steps**: Group by `step_index`, execute all members in parallel at each index
- **Step indices**: Steps at index N must all complete before index N+1 starts

### Polling Convention

Workers return polling status via result structure:
- Still polling: `{ Status: "Success", Result: { "complete": false } }`
- Complete: `{ Status: "Success", Result: { "complete": true, "data": {...} } }`

Orchestrator checks `Result.complete` to determine if step is still polling.

### Rollback Handling

- Steps can specify `on_failure` referencing a rollback sequence name
- Rollback sequences are defined in runbook YAML under `rollbacks`
- Rollback steps execute fire-and-forget (no status tracking)

### Status Transitions

**Batch statuses:**
- `detected` → `init_dispatched` → `active` → `completed` / `failed`

**Phase execution statuses:**
- `pending` → `dispatched` → `completed` / `failed` / `skipped`

**Step execution statuses:**
- `pending` → `dispatched` → `succeeded` / `failed` / `polling` → `poll_timeout` / `cancelled`

## Configuration

Settings are bound via `IOptions<OrchestratorSettings>` from the `Orchestrator` config section:

- `Orchestrator__SqlConnectionString` – SQL Server connection string
- `Orchestrator__ServiceBusNamespace` – FQDN of the Service Bus namespace
- `Orchestrator__OrchestratorEventsTopicName` – Topic name (default: `orchestrator-events`)
- `Orchestrator__OrchestratorSubscriptionName` – Subscription name (default: `orchestrator`)
- `Orchestrator__WorkerJobsTopicName` – Topic for dispatching jobs (default: `worker-jobs`)
- `Orchestrator__WorkerResultsTopicName` – Topic for receiving results (default: `worker-results`)
- `Orchestrator__WorkerResultsSubscriptionName` – Subscription name (default: `orchestrator`)

Service Bus connection uses DefaultAzureCredential:
- `ServiceBusConnection__fullyQualifiedNamespace` – FQDN for trigger binding

## SQL Tables (Read/Write)

**Orchestrator writes to:**
- `batches.status` (active, completed, failed)
- `phase_executions.status`, `completed_at`
- `step_executions.*` (status, job_id, result_json, error_message, timestamps, polling fields)
- `init_executions.*` (same fields)

**Orchestrator reads from:**
- `runbooks.yaml_content` (for rollback definitions, on_member_removed)
- `runbook_{name}_v{version}` dynamic tables (member data for template resolution)

## Infrastructure

Deploy via Bicep template at `infra/automation/orchestrator/deploy.bicep`:

```bash
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/orchestrator/deploy.bicep \
  --parameters environmentName=dev \
               serviceBusNamespaceName=matoolkit-sb \
               sqlServerName=sql-scheduler-dev \
               sqlDatabaseName=sqldb-scheduler-dev \
               sqlConnectionString='...' \
               keyVaultName=kv-matoolkit \
               logAnalyticsWorkspaceId='/subscriptions/.../workspaces/...'
```

Creates:
- Flex Consumption Function App (.NET 8 isolated)
- Storage Account (for Functions runtime)
- Application Insights
- Service Bus topics (`worker-jobs`, `worker-results`) and subscription
- RBAC: Service Bus Data Sender + Receiver on namespace
- RBAC: Key Vault Secrets User on vault
