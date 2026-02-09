# CLAUDE.md -- Orchestrator

## Project Overview

C# Azure Functions project using the **isolated worker model** (.NET 8, Functions v4). The orchestrator is the job dispatch and result processing engine for the M&A Toolkit migration pipeline. It receives events from the scheduler via Service Bus, dispatches jobs to cloud workers, processes results, manages step progression, and handles rollback sequences.

## Build and Run

```bash
# Build
dotnet build src/automation/orchestrator/src/Orchestrator.Functions/

# Run tests (50 tests)
dotnet test src/automation/orchestrator/tests/Orchestrator.Functions.Tests/

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
          StepDefinition.cs           # Step with name, worker_id, function, params, on_failure, poll, retry
          PollConfig.cs               # Poll interval and timeout
          RetryConfig.cs              # Retry max_retries and interval
          RollbackSequence.cs         # Named rollback sequence
          MultiValuedColumnConfig.cs  # Column name + format
        Messages/
          BatchInitMessage.cs         # batch-init event (from scheduler)
          PhaseDueMessage.cs          # phase-due event (from scheduler)
          MemberAddedMessage.cs       # member-added event (from scheduler)
          MemberRemovedMessage.cs     # member-removed event (from scheduler)
          PollCheckMessage.cs         # poll-check event (from scheduler)
          RetryCheckMessage.cs        # retry-check event (self-scheduled)
          WorkerJobMessage.cs         # Job dispatch to worker-jobs topic
          WorkerResultMessage.cs      # Result from worker-results topic
      Services/
        DbConnectionFactory.cs        # Singleton: creates SqlConnection from config
        RunbookParser.cs              # YamlDotNet deserialization
        PhaseEvaluator.cs             # Parses offsets, calculates due_at
        WorkerDispatcher.cs           # Sends jobs to worker-jobs Service Bus topic
        RetryScheduler.cs             # Sends scheduled retry-check messages to orchestrator-events topic
        RollbackExecutor.cs           # Executes rollback sequences on failure
        Repositories/
          RunbookRepository.cs        # Read runbook YAML for rollbacks
          BatchRepository.cs          # Status updates (active, completed, failed)
          MemberRepository.cs         # Read member data
          PhaseExecutionRepository.cs # Status updates, completion tracking
          StepExecutionRepository.cs  # Full lifecycle: dispatched, succeeded, failed, polling
          InitExecutionRepository.cs  # Full lifecycle for init steps
        PhaseProgressionService.cs    # Per-member step/phase/batch progression + failure handling
        Handlers/
          BatchInitHandler.cs         # Process batch-init: create init_executions on demand, dispatch sequentially
          PhaseDueHandler.cs          # Process phase-due: per-member step dispatch
          MemberAddedHandler.cs       # Process member-added: create catch-up steps
          MemberRemovedHandler.cs     # Process member-removed: cancel pending, run cleanup
          PollCheckHandler.cs         # Process poll-check: timeout check, re-dispatch, member failure
          RetryCheckHandler.cs        # Process retry-check: re-dispatch step/init after retry interval
          ResultProcessor.cs          # Process worker results: update status, retry or advance/fail
  tests/
    Orchestrator.Functions.Tests/
      Orchestrator.Functions.Tests.csproj
      Services/
        PhaseProgressionServiceTests.cs   # 20 tests: member progression, failure, phase/batch completion
        Handlers/
          BatchInitHandlerTests.cs        # 7 tests: init creation, idempotency, retry config, dispatch
          ResultProcessorTests.cs         # 12 tests: success/failure routing, terminal guard, retry scenarios
          RetryCheckHandlerTests.cs       # 4 tests: step/init retry dispatch, cancelled skip, not found
          PhaseDueHandlerTests.cs         # 7 tests: per-member dispatch, mixed progress, edge cases
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
   - `batch-init`: New batch ready for init step execution (orchestrator creates init_executions on demand)
   - `phase-due`: Phase is due, dispatch step executions
   - `member-added`: New member joined active batch
   - `member-removed`: Member left batch
   - `poll-check`: Time to re-poll a polling step
   - `retry-check`: Scheduled by orchestrator itself — time to re-dispatch a failed step/init after retry interval

2. **Orchestrator → Worker** via `worker-jobs` topic:
   - `WorkerJobMessage`: Job ID, worker pool, function, parameters, correlation data

3. **Worker → Orchestrator** via `worker-results` topic:
   - `WorkerResultMessage`: Job ID, status, result/error, correlation data

### Step Execution Model

- **Init steps**: Created on demand by `BatchInitHandler` (like `PhaseDueHandler` for step_executions), then executed sequentially (one at a time) within a batch. Version-aware idempotency: checks `RunbookVersion` to avoid duplicates during version transitions.
- **Phase steps**: Per-member independent progression — each member advances through steps at their own pace
- **Member isolation**: A failed member is marked `failed`, all their remaining steps (across all phases) are cancelled, and healthy members continue unblocked

### Per-Member Progression (`PhaseProgressionService`)

The `PhaseProgressionService` centralizes all progression logic, used by `ResultProcessor`, `PhaseDueHandler`, and `PollCheckHandler`:

- **`CheckMemberProgressionAsync`** — After a step succeeds, walks that member's steps in order and dispatches their next pending step. Each member progresses independently.
- **`HandleMemberFailureAsync`** — After a step fails (with retries exhausted or no retries configured) or poll times out, marks the member as `failed` and cancels all their non-terminal steps across ALL phases. Then checks if the phase is complete.
- **`CheckPhaseCompletionAsync`** — When all steps in a phase are terminal: marks phase `Completed` if at least one member fully succeeded, or `Failed` if no member did.
- **`CheckBatchCompletionAsync`** — When all phases are terminal: marks batch `Completed` if at least one phase completed, or `Failed` if none did.

### Member Data & Template Resolution

Member data is stored as a JSON document (`data_json` column) on each `batch_members` row, snapshotted at insertion time. Handlers deserialize `BatchMemberRecord.DataJson` into `Dictionary<string, string>` for use in template resolution.

**Template resolution** uses the shared `ITemplateResolver` from `MaToolkit.Automation.Shared`. Templates use `{{ColumnName}}` syntax resolved from the member data dictionary. Special variables `{{_batch_id}}` and `{{_batch_start_time}}` are always available. `ResolveInitParams` resolves only batch-level variables (no member data). Throws `TemplateResolutionException` for unresolved variables.

### Polling Convention

Workers return polling status via result structure:
- Still polling: `{ Status: "Success", Result: { "complete": false } }`
- Complete: `{ Status: "Success", Result: { "complete": true, "data": {...} } }`

Orchestrator checks `Result.complete` to determine if step is still polling.

### Rollback Handling

- Steps can specify `on_failure` referencing a rollback sequence name
- Rollback sequences are defined in runbook YAML under `rollbacks`
- Rollback steps execute fire-and-forget (no status tracking)

### Step Retry

Configurable retry logic for transient failures. Runbooks can specify a global `retry` config (applies to all steps including init) and individual steps can override it.

**YAML config:**
```yaml
retry:              # Global — applies to all steps
  max_retries: 2
  interval: 1m

phases:
  - name: example
    steps:
      - name: flaky-step
        retry:            # Step-level override (replaces global entirely)
          max_retries: 3
          interval: 30s
      - name: polling-step
        poll: { ... }
        retry:
          max_retries: 0  # Explicitly disables global retry
```

**Resolution:** Step-level `retry` overrides global `retry` entirely (not merged). Effective retry config is resolved once at step creation time and stored on the execution record (`max_retries`, `retry_interval_sec`, `retry_count`, `retry_after`).

**Retry flow:**
1. Step fails → `ResultProcessor` marks it `failed` via `SetFailedAsync`
2. If `max_retries` is set and `retry_count < max_retries`: `SetRetryPendingAsync` resets status to `pending`, increments `retry_count`, clears `job_id`/`completed_at`
3. `RetryScheduler` sends a scheduled `retry-check` message to the orchestrator-events topic with `ScheduledEnqueueTime` set to the retry interval
4. When the message arrives, `RetryCheckHandler` verifies the step is still `pending` (could have been cancelled), then re-dispatches via `WorkerDispatcher`

**What does NOT retry:**
- **Poll timeout** — polling steps already manage their own duration via `poll.timeout`; retrying would silently extend the configured timeout
- **Steps with `max_retries: 0`** — explicit opt-out even when global retry is configured
- **Steps with no retry config** (and no global config) — behave as before (immediate failure → rollback → member failure)

**Job ID format for retries:** `step-{id}-retry-{retryCount}` / `init-{id}-retry-{retryCount}` — ensures uniqueness for Service Bus deduplication.

### Status Transitions

**Batch statuses:**
- `detected` → `init_dispatched` → `active` → `completed` / `failed`

**Member statuses:**
- `active` → `removed` (removed from batch) / `failed` (step failure isolated this member)

**Phase execution statuses:**
- `pending` → `dispatched` → `completed` (≥1 member succeeded) / `failed` (no member succeeded) / `skipped`

**Step execution statuses:**
- `pending` → `dispatched` → `succeeded` / `failed` / `polling` → `poll_timeout` / `cancelled`
- Retry loop: `failed` → `pending` (via `SetRetryPendingAsync`) → `dispatched` → ...

### Race Condition Safety

- **Two results arrive simultaneously for same phase:** Both call `CheckPhaseCompletionAsync`, both query all steps. Only one sees all steps terminal. `SetCompletedAsync`/`SetFailedAsync` use `WHERE status = 'dispatched'` guard — only one call succeeds.
- **Member failure + step success for same member:** `SetFailedAsync` on member uses `WHERE status = 'active'` — idempotent. `SetCancelledAsync` on steps uses status guards — won't cancel already-succeeded steps.
- **Duplicate phase-due messages:** `PhaseDueHandler` is idempotent — step creation checks for existing steps, dispatch only targets `pending` steps.
- **Result for already-cancelled step:** `ResultProcessor` has a terminal-state guard — ignores results for steps already in terminal status.
- **Retry-check arrives after step cancelled:** `RetryCheckHandler` checks `status == pending` before dispatching — if the step was cancelled between scheduling and arrival, the retry is silently skipped.
- **SetRetryPendingAsync guard:** Uses `WHERE status IN ('failed', 'poll_timeout')` — only transitions from expected states, returns false if step was already moved by another handler.

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
- `batch_members.status` (failed), `batch_members.failed_at`
- `phase_executions.status`, `completed_at`
- `step_executions.*` (status, job_id, result_json, error_message, timestamps, polling fields, retry fields)
- `init_executions.*` (same fields)

**Orchestrator reads from:**
- `runbooks.yaml_content` (for rollback definitions, on_member_removed)
- `batch_members.data_json` (member data JSON for template resolution)

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
