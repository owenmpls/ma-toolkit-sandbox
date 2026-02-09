# Automation Subsystem Architecture

Comprehensive system architecture reference for the M&A Toolkit automation subsystem. This document consolidates all component architectures, inter-service contracts, data models, protocols, and infrastructure topology into a single reference.

---

## Table of Contents

- [System Overview](#system-overview)
- [Component Summary](#component-summary)
- [Data Flow](#data-flow)
- [Infrastructure Topology](#infrastructure-topology)
- [Service Bus Architecture](#service-bus-architecture)
- [State Machines](#state-machines)
- [SQL Schema](#sql-schema)
- [Runbook YAML Format](#runbook-yaml-format)
- [Template Resolution](#template-resolution)
- [Protocols](#protocols)
- [Security Model](#security-model)
- [Component Deep Dives](#component-deep-dives)
- [Deployment Topology](#deployment-topology)

---

## System Overview

The automation subsystem is an event-driven, queue-based system for Microsoft tenant integration and migration, including but not limited to Microsoft 365. It automates the lifecycle of migration batches: discovering members from external data sources, scheduling phased execution, dispatching jobs to workers, processing results, and handling retries, polling, and rollbacks.

The system follows a strict separation of concerns:

- **Detection and timing** are owned by the scheduler.
- **Coordination and progression** are owned by the orchestrator.
- **Execution** is owned by the cloud worker.
- **User interaction** is owned by the admin API and CLI.

All inter-component communication (except the admin API/CLI HTTP interface) flows through Azure Service Bus, providing durability, decoupling, and resilience to component restarts.

```
+----------------+                              +----------------+
| Data Sources   |                              | Admin CLI      |
| Dataverse/     |                              | (matoolkit)    |
| Databricks     |                              +-------+--------+
+-------+--------+                                      |
        ^                                          HTTPS/JWT
        | query                                         |
        |                                               v
+-------+--------+                              +----------------+
|   Scheduler    |--- read/write batches, --->  |   Admin API    |
| (Azure Funcs,  |    members, phases           | (Azure Funcs)  |
|  5-min timer)  |                              +-------+--------+
+--+----+--------+                                      |
   |    |    ^                                    read/write
   |    |    | read YAML,                          SQL DB
   |    |    | write batches/members/phases              |
   |    |    v                                          |
   |    |  +------------------------------------------+-+
   |    |  |              SQL DB                        |
   |    |  |  runbooks, batches, members, phases,       |
   |    |  |  step_executions, init_executions,         |
   |    |  |  runbook_automation_settings               |
   |    |  +------------------------------------------+-+
   |    |                                      ^        ^
   |    |                          read YAML,  |        |
   |    |                        write steps,  |        |
   |    |                     update statuses  |        |
   |    |                                      |        |
   |    |  orchestrator-events     +-----------+--------+
   |    +------------------------->|   Orchestrator      |
   |                               | (Azure Funcs)      |
   |  (batch-init, phase-due,      +--+---------+-------+
   |   poll-check, retry-check,       |         ^
   |   member-added/removed)          |         |
   |                       worker-jobs|         |worker-results
   |                                  v         |
   |                               +--+---------+-------+
   |                               |   Cloud Worker      |
   +--- no DB connection -----X   | (PowerShell, ACA)   |
                                   |   Graph + EXO ops   |
                                   +---------------------+
```

---

## Component Summary

### Scheduler

| Property | Value |
|----------|-------|
| Runtime | C# Azure Functions (isolated worker, .NET 8) |
| Trigger | Timer (5-minute CRON: `0 */5 * * * *`) |
| Hosting | Azure Functions Flex Consumption |
| Role | Timing and detection engine |

Reads YAML runbook definitions from SQL, queries external data sources (Dataverse via TDS endpoint, Databricks via SQL Statements API), detects new batches, tracks member additions and removals, evaluates phase timing offsets, and dispatches events to the orchestrator via Service Bus. Also monitors polling steps and emits `poll-check` messages when intervals elapse.

### Orchestrator

| Property | Value |
|----------|-------|
| Runtime | C# Azure Functions (isolated worker, .NET 8) |
| Trigger | Service Bus (two subscriptions) |
| Hosting | Azure Functions Flex Consumption |
| Role | Job dispatch and result processing engine |

Consumes events from the scheduler via the `orchestrator-events` topic, dispatches jobs to cloud workers via the `worker-jobs` topic, processes results from the `worker-results` topic, manages step progression with per-member independent advancement, and handles retry scheduling and rollback sequences.

### Admin API

| Property | Value |
|----------|-------|
| Runtime | C# Azure Functions (isolated worker, .NET 8) |
| Trigger | HTTP |
| Hosting | Azure Functions Flex Consumption |
| Role | RESTful control plane |

Exposes 20 endpoints for managing runbooks, controlling automation, CSV uploads, batch management, and execution tracking. Secured with Entra ID JWT bearer authentication: write operations require the `Admin` app role, read operations require any authenticated user.

### Admin CLI

| Property | Value |
|----------|-------|
| Runtime | Cross-platform .NET CLI tool |
| Tool name | `matoolkit` |
| Role | CLI client for the Admin API |

Built with `System.CommandLine` for parsing and `Spectre.Console` for rich terminal output. Provides full coverage of every Admin API endpoint with Entra ID device code flow authentication and persistent MSAL token caching.

### Cloud Worker

| Property | Value |
|----------|-------|
| Runtime | PowerShell 7.4 |
| Hosting | Azure Container Apps (scale-to-zero) |
| Role | Migration function execution |

Executes migration operations against a target Microsoft 365 tenant using MgGraph and Exchange Online PowerShell modules. Uses a RunspacePool with per-runspace authenticated sessions for parallel execution. Certificate-based auth via PFX from Key Vault. KEDA scaler monitors the Service Bus subscription and scales from 0 to 1 when messages arrive. Idle timeout (300s default) triggers graceful exit so ACA scales back to zero.

---

## Data Flow

The end-to-end data flow covers automated batch discovery through the scheduler, manual batch creation through the admin API, and the shared orchestration and execution path.

### Automated Batch Lifecycle

1. **Admin publishes runbook** (YAML) via Admin API. Stored in the `runbooks` SQL table.
2. **Scheduler timer fires** (every 5 minutes). Reads all active runbooks, parses YAML, queries configured data sources.
3. **Batch detection.** Query results are grouped by batch time. New batch groups create batch, member, and phase execution records. Existing batches get member diffs (adds/removes).
5. **Init dispatch.** If the runbook defines `init` steps, the scheduler publishes a `batch-init` message. Otherwise, the batch goes directly to `active` status.
6. **Orchestrator processes batch-init.** Creates init_executions on demand, dispatches init steps sequentially to the `worker-jobs` topic.
7. **Worker executes function.** Returns result to `worker-results` topic.
8. **Orchestrator processes result.** Advances init steps. When all init steps succeed, batch status becomes `active`.
9. **Phase timing evaluation.** On each tick, the scheduler evaluates pending phases where `due_at <= now` and publishes `phase-due` messages.
10. **Orchestrator processes phase-due.** Creates step_executions per member, dispatches per member (parallel within a step_index, sequential across step indices).
11. **Per-member progression.** Each member advances independently through steps at their own pace. Failed members are isolated without blocking healthy members.
12. **Polling.** If a worker returns `{complete: false}`, the orchestrator sets the step to `polling` status. The scheduler detects elapsed intervals and sends `poll-check` messages. The orchestrator re-dispatches until the worker returns `{complete: true}` or the timeout expires.
13. **Retry.** Failed steps with retry configuration are reset to `pending` with a scheduled `retry-check` message. The orchestrator re-dispatches after the retry interval.
14. **Rollback.** Failed steps (with retries exhausted) that have `on_failure` set trigger a rollback sequence loaded from the runbook YAML. Rollback steps are fire-and-forget.
15. **Phase completion.** A phase completes when all member steps reach terminal status: `completed` if at least one member succeeded, `failed` if none did.
16. **Batch completion.** A batch completes when all phases reach terminal status.

### Manual Batch Lifecycle

1. **Admin creates batch** via `POST /api/batches` with a CSV file. The Admin API creates the batch with `is_manual=1`, `batch_start_time=NULL`, inserts members, and creates phase executions with `due_at=NULL`.
2. **Admin advances batch** via `POST /api/batches/{id}/advance`. The Admin API dispatches init steps (if pending), then each phase in order, publishing to Service Bus.
3. From this point, processing follows the same orchestrator path as automated batches.

### Member Synchronization

On each scheduler tick, member data (`data_json` on `batch_members`) is refreshed for active members still present in query results. This ensures downstream phases always use the latest attribute values when resolving templates.

---

## Infrastructure Topology

All infrastructure is defined in Bicep templates and deployed in two stages.

### Network

| Subnet | CIDR | Purpose | Delegation |
|--------|------|---------|------------|
| `snet-scheduler` | 10.0.1.0/24 | Scheduler Function App | Microsoft.Web/serverFarms |
| `snet-orchestrator` | 10.0.2.0/24 | Orchestrator Function App | Microsoft.Web/serverFarms |
| `snet-admin-api` | 10.0.3.0/24 | Admin API Function App | Microsoft.Web/serverFarms |
| `snet-cloud-worker` | 10.0.4.0/23 | ACA environment | None |
| `snet-private-endpoints` | 10.0.10.0/24 | Private endpoints | None |

VNet address space: `10.0.0.0/16`. Function App subnets have `Microsoft.Storage` service endpoints enabled for storage account network rules.

### Private Endpoints

All private endpoints reside in `snet-private-endpoints` (10.0.10.0/24).

| Endpoint | Target Resource | Group ID | Created In |
|----------|----------------|----------|------------|
| `*-pe-kv` | Key Vault | vault | shared |
| `*-pe-sb` | Service Bus | namespace | shared |
| `*-pe-sql` | SQL Server | sqlServer | scheduler-orchestrator |
| `*-pe-st-blob` (x3) | Storage Account (per app) | blob | scheduler-orchestrator, admin-api |
| `*-pe-st-queue` (x3) | Storage Account (per app) | queue | scheduler-orchestrator, admin-api |
| `*-pe-st-table` (x3) | Storage Account (per app) | table | scheduler-orchestrator, admin-api |

Total: 1 KV + 1 SB + 1 SQL + 9 Storage = **12 private endpoints**.

### Private DNS Zones

6 zones created in the shared template and linked to the VNet:

| Zone | Resolves |
|------|----------|
| `privatelink.database.windows.net` | SQL Server PE |
| `privatelink.vaultcore.azure.net` | Key Vault PE |
| `privatelink.servicebus.windows.net` | Service Bus PE |
| `privatelink.blob.core.windows.net` | Storage blob PEs |
| `privatelink.queue.core.windows.net` | Storage queue PEs |
| `privatelink.table.core.windows.net` | Storage table PEs |

### Network Traffic Flows

**VNet-internal (via private endpoints + private DNS):**
- SQL queries — scheduler, orchestrator, admin API to SQL Server
- Storage operations — each Function App to its own storage account (blob, queue, table)
- Key Vault secret retrieval — all components to Key Vault (connection strings, certificates)
- Service Bus messaging — all components to Service Bus (topics/subscriptions)

**Azure backbone (not routed through VNet):**
- ACR image pulls — ACA pulls cloud worker container images from Container Registry
- Log Analytics / Application Insights — telemetry from all components
- Microsoft Graph API — cloud worker to target tenant (Entra ID operations)
- Exchange Online — cloud worker to target tenant (mailbox operations)
- Dataverse TDS endpoint — scheduler data source queries
- Databricks SQL Statements API — scheduler data source queries

### Network Access Controls

| Resource | Public Access | Firewall Rule |
|----------|-------------|---------------|
| SQL Server | `publicNetworkAccess: Disabled` | Private endpoint only |
| Key Vault | `publicNetworkAccess: Enabled` (firewall enforced) | `defaultAction: Deny`, `bypass: AzureServices` |
| Storage Accounts (x3) | Firewall enforced | `defaultAction: Deny`, `bypass: AzureServices`, subnet allow-list + PEs |
| Service Bus | Private endpoint | PE in shared subnet |
| Function Apps (x3) | `publicNetworkAccess: Disabled` (when VNet-integrated) | `vnetRouteAllEnabled: true` |
| ACA Environment | `internal: true` (when VNet-integrated) | No public ingress |

### Shared Resources (Stage 1)

| Resource | SKU/Tier | Purpose |
|----------|----------|---------|
| Log Analytics Workspace | PerGB2018 | Centralized logging for all App Insights instances |
| Service Bus Namespace | Standard | Inter-component messaging |
| Key Vault | Standard, RBAC | Secrets storage (SQL connection strings, SB connection for KEDA, worker certificates) |
| Container Registry | Basic | Cloud worker container images |
| Private DNS Zones | -- | SQL, Key Vault, Service Bus, Storage (blob, queue, table) |
| Private Endpoints | -- | Key Vault, Service Bus (in shared PE subnet) |

Key Vault configuration: RBAC authorization enabled, soft delete with 7-day retention, purge protection enabled. Firewall denies public traffic by default with Azure Services bypass.

### Component Resources (Stage 2)

**Scheduler + Orchestrator** (single Bicep template):
- Azure SQL Serverless (GP_S_Gen5_1, 32 GB max, 60-minute auto-pause, Entra-only authentication)
- Two Flex Consumption Function Apps (scheduler, orchestrator)
- Two Storage Accounts (Functions runtime)
- Two Application Insights instances
- SQL and Storage private endpoints
- Service Bus subscriptions and RBAC assignments

**Admin API:**
- Flex Consumption Function App
- Storage Account
- Application Insights
- Storage private endpoints
- RBAC assignments (Key Vault, Storage, Service Bus)

**Cloud Worker:**
- ACA Environment with VNet integration
- ACA App with KEDA Service Bus scaler (min replicas: 0, max replicas: 1)
- Per-worker Service Bus subscription with SQL filter on `WorkerId` property
- RBAC assignments (ACR Pull, Key Vault, Service Bus)

---

## Service Bus Architecture

### Topics

| Topic | Duplicate Detection | TTL | Purpose |
|-------|-------------------|-----|---------|
| `orchestrator-events` | 10-minute window | 7 days | Scheduler-to-orchestrator events + orchestrator self-scheduled retries |
| `worker-jobs` | 10-minute window | 7 days | Orchestrator-to-worker job dispatch |
| `worker-results` | 10-minute window | 7 days | Worker-to-orchestrator results |

All topics: 1024 MB max size, partitioning disabled.

### Subscriptions

| Topic | Subscription | Consumer | Filter |
|-------|-------------|----------|--------|
| `orchestrator-events` | `orchestrator` | Orchestrator | All messages (no filter) |
| `worker-jobs` | `worker-{workerId}` | Cloud Worker | SQL filter: `WorkerId = '{workerId}'` |
| `worker-results` | `orchestrator` | Orchestrator | All messages (no filter) |

Orchestrator subscription configuration: max delivery count 10, lock duration 1 minute, default TTL 14 days, dead-lettering on expiration enabled.

### Message Routing

All messages on the `orchestrator-events` topic include a `MessageType` application property for identification. The `worker-jobs` topic uses a `WorkerId` application property for per-worker filtering.

### Message Types (8 Total)

#### 1. batch-init

Published by the scheduler when a new batch is detected and the runbook has `init` steps. Also published by the admin API when advancing a manual batch with pending init.

```json
{
  "BatchId": 42,
  "RunbookName": "contoso-mailbox-migration",
  "RunbookVersion": 3
}
```

Application property: `MessageType = "batch-init"`

#### 2. phase-due

Published by the scheduler when a phase's `due_at` time has passed. Also published by the admin API when advancing a manual batch to the next phase.

```json
{
  "BatchId": 42,
  "RunbookName": "contoso-mailbox-migration",
  "RunbookVersion": 3,
  "PhaseName": "pre-notification",
  "PhaseExecutionId": 108
}
```

Application property: `MessageType = "phase-due"`

#### 3. member-added

Published by the scheduler when a new member appears in query results for an existing active batch.

```json
{
  "BatchId": 42,
  "MemberKey": "newuser@contoso.com",
  "BatchMemberId": 505
}
```

Application property: `MessageType = "member-added"`

#### 4. member-removed

Published by the scheduler when a member is no longer returned by the data source query.

```json
{
  "BatchId": 42,
  "MemberKey": "removeduser@contoso.com",
  "BatchMemberId": 503
}
```

Application property: `MessageType = "member-removed"`

#### 5. poll-check

Published by the scheduler when a polling step's interval has elapsed.

```json
{
  "StepExecutionId": 1042,
  "IsInitStep": false
}
```

Application property: `MessageType = "poll-check"`

#### 6. retry-check

Self-scheduled by the orchestrator using `ScheduledEnqueueTime`. Published after a step fails and has retry attempts remaining.

```json
{
  "StepExecutionId": 1042,
  "IsInitStep": false
}
```

Application property: `MessageType = "retry-check"`

#### 7. WorkerJobMessage (Orchestrator to Worker)

Published by the orchestrator to the `worker-jobs` topic.

```json
{
  "JobId": "step-123-attempt-1",
  "BatchId": 42,
  "WorkerId": "worker-01",
  "FunctionName": "New-EntraUser",
  "Parameters": {
    "DisplayName": "John Doe",
    "UserPrincipalName": "jdoe@contoso.com"
  },
  "CorrelationData": {
    "StepExecutionId": 123,
    "IsInitStep": false,
    "RunbookName": "contoso-mailbox-migration",
    "RunbookVersion": 1
  }
}
```

Application property: `WorkerId = "worker-01"` (used for subscription filtering).

#### 8. WorkerResultMessage (Worker to Orchestrator)

Published by the cloud worker to the `worker-results` topic.

```json
{
  "JobId": "step-123-attempt-1",
  "Status": "Success",
  "ResultType": "Object",
  "Result": { "complete": true, "data": { "UserId": "abc-123" } },
  "Error": null,
  "DurationMs": 1234,
  "Timestamp": "2025-03-15T10:30:00Z",
  "CorrelationData": {
    "StepExecutionId": 123,
    "IsInitStep": false,
    "RunbookName": "contoso-mailbox-migration",
    "RunbookVersion": 1
  }
}
```

Error structure (on failure):

```json
{
  "Error": {
    "Message": "User not found",
    "Type": "Microsoft.Graph.ServiceException",
    "IsThrottled": false,
    "Attempts": 1
  }
}
```

---

## State Machines

### Batch Status

```
Automated:  detected --> init_dispatched --> active --> completed
                                                   \-> failed

Manual:     detected --> init_dispatched --> active --> completed
                                                   \-> failed
```

- `detected`: Batch created (by scheduler from query results, or by admin API for manual batches). Init not yet dispatched.
- `init_dispatched`: Init steps dispatched to orchestrator.
- `active`: Init complete (or no init steps), ready for phase processing.
- `completed`: All phases reached terminal status with at least one success.
- `failed`: Init step failed, or all phases failed.

### Member Status

```
active --> removed   (no longer in data source query results)
       \-> failed    (step failure isolated this member)
```

### Phase Execution Status

```
pending --> dispatched --> completed  (at least one member succeeded)
                       \-> failed     (no member succeeded)
                       \-> skipped    (overdue_behavior = ignore)
                       \-> superseded (replaced by new runbook version)
```

### Step/Init Execution Status

```
pending --> dispatched --> succeeded
                       \-> failed --> pending (retry) --> dispatched --> ...
                       \-> polling --> succeeded
                                   \-> poll_timeout
                       \-> cancelled
```

- `pending`: Created, not yet sent to a worker.
- `dispatched`: Job sent to worker, awaiting result.
- `succeeded`: Worker returned success.
- `failed`: Worker returned error. May trigger retry or rollback.
- `polling`: Worker returned `{complete: false}`. Awaiting next poll check.
- `poll_timeout`: Polling exceeded configured timeout. Treated as failure for rollback purposes but does NOT trigger retry.
- `cancelled`: Step cancelled (member removed, member failed on earlier step, or batch cancelled).

Retry loop: `failed` -> `pending` (via `SetRetryPendingAsync`, guarded by `WHERE status IN ('failed', 'poll_timeout')`) -> `dispatched` -> ...

---

## SQL Schema

The system uses 7 core tables.

### runbooks

| Column | Type | Description |
|--------|------|-------------|
| Id | INT (PK, identity) | Auto-increment primary key |
| Name | NVARCHAR | Runbook name |
| Version | INT | Version number (incremented on each publish) |
| YamlContent | NVARCHAR(MAX) | Raw YAML definition |
| DataTableName | NVARCHAR | Vestigial — populated on publish but unused at runtime |
| IsActive | BIT | Whether this version is the current active version |
| OverdueBehavior | NVARCHAR | `"rerun"` or `"ignore"` -- controls past-due phase handling on version transitions |
| IgnoreOverdueApplied | BIT | Whether the ignore behavior was already applied |
| RerunInit | BIT | Whether to re-run init steps on version transition |
| CreatedAt | DATETIME2 | Creation timestamp |
| LastError | NVARCHAR(MAX) | Last processing error from scheduler |
| LastErrorAt | DATETIME2 | When the last error occurred |

Only one version is active per runbook name at any time. Publishing a new version deactivates the previous one.

### batches

| Column | Type | Description |
|--------|------|-------------|
| Id | INT (PK, identity) | Auto-increment primary key |
| RunbookId | INT (FK -> runbooks) | Associated runbook |
| BatchStartTime | DATETIME2 | Reference time for phase offsets; NULL for manual batches |
| Status | NVARCHAR | See batch status state machine |
| DetectedAt | DATETIME2 | When the batch was created |
| InitDispatchedAt | DATETIME2 | When init was dispatched to orchestrator |
| IsManual | BIT | Whether this is a manually-created batch |
| CreatedBy | NVARCHAR | User identity who created the batch (manual only) |
| CurrentPhase | NVARCHAR | Current phase name being advanced (manual only) |

### batch_members

| Column | Type | Description |
|--------|------|-------------|
| Id | INT (PK, identity) | Auto-increment primary key |
| BatchId | INT (FK -> batches) | Parent batch |
| MemberKey | NVARCHAR | Primary key value from data source |
| DataJson | NVARCHAR(MAX) | Member data snapshot as JSON |
| WorkerDataJson | NVARCHAR(MAX) | Accumulated worker output data from `output_params` |
| Status | NVARCHAR | `"active"`, `"removed"`, or `"failed"` |
| AddedAt | DATETIME2 | When the member was added |
| RemovedAt | DATETIME2 | When removed (NULL if active) |
| FailedAt | DATETIME2 | When marked failed (NULL if not failed) |
| AddDispatchedAt | DATETIME2 | When `member-added` message was dispatched |
| RemoveDispatchedAt | DATETIME2 | When `member-removed` message was dispatched |

Member data is refreshed on every scheduler tick for active members still present in query results, ensuring downstream template resolution uses the latest attribute values.

### phase_executions

| Column | Type | Description |
|--------|------|-------------|
| Id | INT (PK, identity) | Auto-increment primary key |
| BatchId | INT (FK -> batches) | Parent batch |
| PhaseName | NVARCHAR | Phase name from runbook YAML |
| OffsetMinutes | INT | Offset from batch_start_time in minutes |
| DueAt | DATETIME2 | Absolute due time; NULL for manual batches |
| RunbookVersion | INT | Which runbook version created this execution |
| Status | NVARCHAR | See phase status state machine |
| DispatchedAt | DATETIME2 | When dispatched to orchestrator |
| CompletedAt | DATETIME2 | When all member steps reached terminal status |

### step_executions

| Column | Type | Description |
|--------|------|-------------|
| Id | INT (PK, identity) | Auto-increment primary key |
| PhaseExecutionId | INT (FK -> phase_executions) | Parent phase |
| BatchMemberId | INT (FK -> batch_members) | Target member |
| StepName | NVARCHAR | Step name from runbook YAML |
| StepIndex | INT | Order within phase (0-based) |
| WorkerId | NVARCHAR | Target worker pool identifier |
| FunctionName | NVARCHAR | Resolved PowerShell function name |
| ParamsJson | NVARCHAR(MAX) | Resolved parameters as JSON |
| Status | NVARCHAR | See step status state machine |
| JobId | NVARCHAR | Worker job ID (set on dispatch) |
| ResultJson | NVARCHAR(MAX) | Worker result payload |
| ErrorMessage | NVARCHAR(MAX) | Error details on failure |
| DispatchedAt | DATETIME2 | When dispatched to worker |
| CompletedAt | DATETIME2 | When result was received |
| IsPollStep | BIT | Whether this step uses polling |
| PollIntervalSec | INT | Seconds between poll checks |
| PollTimeoutSec | INT | Maximum seconds before timeout |
| PollStartedAt | DATETIME2 | When polling began |
| LastPolledAt | DATETIME2 | Last poll check time |
| PollCount | INT | Number of poll checks performed |
| OnFailure | NVARCHAR | Rollback sequence name from YAML |
| RetryCount | INT | Current retry attempt number |
| MaxRetries | INT | Maximum allowed retry attempts |
| RetryIntervalSec | INT | Seconds between retry attempts |
| RetryAfter | DATETIME2 | Scheduled time for next retry |

### init_executions

Same columns as `step_executions` except:
- Has `BatchId` (FK -> batches) instead of `PhaseExecutionId`
- Has `RunbookVersion` instead of `BatchMemberId`

Init steps run once per batch (not per member) and are dispatched sequentially.

### runbook_automation_settings

| Column | Type | Description |
|--------|------|-------------|
| RunbookName | NVARCHAR (PK) | Runbook name |
| AutomationEnabled | BIT | Whether automated batch detection is enabled |
| EnabledAt | DATETIME2 | When automation was enabled |
| EnabledBy | NVARCHAR | User identity who enabled |
| DisabledAt | DATETIME2 | When automation was disabled |
| DisabledBy | NVARCHAR | User identity who disabled |

Disabling automation only stops new batch creation. Existing in-flight batches continue processing.

---

## Runbook YAML Format

A runbook is a YAML document that defines a migration workflow. It specifies data sources, batch grouping, phase timing, step execution, rollback sequences, and member lifecycle handlers.

### Top-Level Structure

```yaml
name: <string>                    # Required. Must match the name in the publish request.
description: <string>             # Optional. Human-readable description.
data_source: <DataSourceConfig>   # Required. External data source configuration.
retry: <RetryConfig>              # Optional. Global retry config for all steps.
init: <list of StepDefinition>    # Optional. Steps run once per batch before phases.
phases: <list of PhaseDefinition> # Required. At least one phase.
on_member_removed: <list of StepDefinition>  # Optional. Steps run when a member leaves.
rollbacks:                        # Optional. Named rollback sequences.
  <name>: <list of StepDefinition>
```

### data_source

```yaml
data_source:
  type: <string>                  # "dataverse" or "databricks"
  connection: <string>            # Environment variable name for connection string/URL
  warehouse_id: <string>          # Databricks only: env var name for warehouse ID
  query: <string>                 # SQL query to execute
  primary_key: <string>           # Column that uniquely identifies each member
  batch_time_column: <string>     # Column containing batch start time (scheduled mode)
  batch_time: <string>            # "immediate" for on-demand batching (alternative)
  multi_valued_columns:           # Optional: columns with delimited or array values
    - name: <string>
      format: <string>            # "semicolon_delimited", "comma_delimited", "json_array"
```

**Scheduled batching** (`batch_time_column`): Members with the same batch time value are grouped into the same batch. Phase offsets are calculated relative to this time.

**Immediate batching** (`batch_time: immediate`): Batch time is set to current UTC rounded to the nearest 5-minute interval. Members already in an active batch for the same runbook are filtered out.

### phases

```yaml
phases:
  - name: <string>         # Unique name within the runbook
    offset: <string>       # When this phase fires relative to batch_start_time
    steps:
      - <StepDefinition>
```

**Offset format** uses `T-` notation:

| Offset | Meaning | Stored as offset_minutes |
|--------|---------|-------------------------|
| `T-5d` | 5 days before batch_start_time | 7200 |
| `T-4h` | 4 hours before | 240 |
| `T-30m` | 30 minutes before | 30 |
| `T-90s` | 90 seconds before (rounded up to 2 minutes) | 2 |
| `T-0` | At exactly batch_start_time | 0 |

Calculation: `due_at = batch_start_time - offset_minutes`. The scheduler evaluates phases as due when `due_at <= now AND status = 'pending'`.

Supported suffixes: `d` (days, x1440 min), `h` (hours, x60 min), `m` (minutes), `s` (seconds, rounded up via `Math.Ceiling(n / 60.0)`).

### steps

```yaml
steps:
  - name: <string>              # Human-readable step name
    worker_id: <string>         # Target worker pool
    function: <string>          # PowerShell function name (supports templates)
    params:                     # Key-value parameters (values support templates)
      ParamName: "{{ColumnName}}"
    output_params:              # Optional: maps result fields to template variables
      TemplateVar: "resultField"
    on_failure: <string>        # Optional: rollback sequence name
    poll:                       # Optional: polling configuration
      interval: <duration>      # e.g., 15m, 24h, 30s
      timeout: <duration>       # e.g., 24h, 7d
    retry:                      # Optional: step-level retry (overrides global)
      max_retries: <int>
      interval: <duration>      # e.g., 1m, 30s
```

Steps appear inside `phases`, `init`, `on_member_removed`, and `rollbacks`.

### retry (global and step-level)

```yaml
# Global retry (applies to all steps including init)
retry:
  max_retries: 2
  interval: 1m

phases:
  - name: example
    steps:
      - name: flaky-step
        retry:               # Step-level override (replaces global entirely)
          max_retries: 3
          interval: 30s
      - name: no-retry-step
        retry:
          max_retries: 0     # Explicitly disables retry even with global config
```

Step-level `retry` overrides global `retry` entirely (not merged). Effective retry configuration is resolved once at step creation time and stored on the execution record.

### init

Optional. Steps that run once per batch when first detected, before any phases fire. Init steps only have access to special variables (`{{_batch_id}}`, `{{_batch_start_time}}`), not per-member data.

```yaml
init:
  - name: create-batch-group
    worker_id: worker-01
    function: New-MigrationBatchGroup
    params:
      BatchId: "{{_batch_id}}"
      StartTime: "{{_batch_start_time}}"
```

If a runbook has no `init` section, the batch goes directly to `active` status.

### on_member_removed

Optional. Steps to execute when a member is removed from a batch (no longer returned by the data source query).

```yaml
on_member_removed:
  - name: disable-forwarding
    worker_id: worker-01
    function: Remove-MailForwarding
    params:
      UserPrincipalName: "{{UserPrincipalName}}"
```

### rollbacks

Optional. Named sequences of steps dispatched when a step with `on_failure` fails.

```yaml
rollbacks:
  undo-mailbox-move:
    - name: revert-mailbox
      worker_id: worker-01
      function: Undo-MailboxMove
      params:
        UserPrincipalName: "{{UserPrincipalName}}"
    - name: notify-admin
      worker_id: worker-01
      function: Send-AdminAlert
      params:
        Subject: "Rollback triggered for {{UserPrincipalName}}"
```

The YAML parser validates that any `on_failure` value references an existing key in `rollbacks`.

---

## Template Resolution

Templates use double-brace syntax (`{{variable_name}}`) and are resolved at phase dispatch time from the member's data.

### Data Column Variables

Any column returned by the data source query can be referenced by name:

```yaml
params:
  Email: "{{UserPrincipalName}}"
  Name: "{{FirstName}} {{LastName}}"
```

Values are looked up from the member's `data_json` (stored on `batch_members`).

### Special Variables

| Variable | Value | Available In |
|----------|-------|-------------|
| `{{_batch_id}}` | Integer batch ID from `batches.id` | Init steps, phase steps |
| `{{_batch_start_time}}` | Batch start time in ISO 8601 (`DateTime.ToString("o")`) | Init steps, phase steps |

### Worker Output Variables

Steps with `output_params` extract named fields from the worker's result payload. These are stored in `batch_members.worker_data_json` and become available as `{{VariableName}}` in subsequent steps for the same member.

Flow: step result -> orchestrator extracts named fields -> stored in `worker_data_json` -> available as template variables in later steps.

### Init Step Limitations

Init steps only have access to special variables (`{{_batch_id}}`, `{{_batch_start_time}}`). They cannot reference per-member data columns because they run once per batch, not per member.

### Function Name Templates

The `function` field also supports templates:

```yaml
function: "New-{{ObjectType}}"
# If ObjectType = "EntraUser", resolves to "New-EntraUser"
```

### Unresolved Variables

If a template variable is not found in the data row or special variables, `TemplateResolutionException` is thrown (in the orchestrator). The step is not dispatched.

---

## Protocols

### Polling Protocol

Used for long-running operations where the worker cannot return a synchronous result.

1. A step with `poll` configuration is created with `is_poll_step = 1`, `poll_interval_sec`, and `poll_timeout_sec` stored on the execution record.
2. The orchestrator dispatches the step to the worker.
3. The worker returns `{ Status: "Success", Result: { "complete": false } }`.
4. The orchestrator sets the step status to `polling`, records `poll_started_at` and `last_polled_at`.
5. The scheduler checks `last_polled_at + poll_interval_sec <= now` on each tick and sends a `poll-check` message.
6. The orchestrator receives `poll-check`, checks if `poll_started_at + poll_timeout_sec < now`:
   - **Timed out:** Set status to `poll_timeout`. Handle as failure (trigger rollback if `on_failure` is set, then member failure via `HandleMemberFailureAsync`).
   - **Not timed out:** Re-dispatch the step to the worker. Update `last_polled_at`, increment `poll_count`.
7. Cycle repeats until the worker returns `{ "complete": true }` (success) or the timeout expires.

Poll timeout does NOT trigger retry, even if retry is configured. Polling steps manage their own duration via `poll.timeout`.

### Retry Protocol

Used for transient failures with configurable retry attempts.

1. A step fails. The `ResultProcessor` marks it `failed` via `SetFailedAsync`.
2. If `max_retries > 0` and `retry_count < max_retries`:
   - `SetRetryPendingAsync` resets status to `pending`, increments `retry_count`, clears `job_id` and `completed_at`.
   - The `RetryScheduler` sends a scheduled `retry-check` message to the `orchestrator-events` topic with `ScheduledEnqueueTime = now + retry_interval_sec`.
3. When the message arrives, `RetryCheckHandler` verifies the step is still `pending` (it could have been cancelled between scheduling and arrival), then re-dispatches via `WorkerDispatcher`.
4. Job ID format for retries: `step-{id}-retry-{retryCount}` or `init-{id}-retry-{retryCount}` to ensure uniqueness for Service Bus duplicate detection.

**What does NOT retry:**
- Poll timeout (polling steps manage their own duration).
- Steps with `max_retries: 0` (explicit opt-out even with global retry config).
- Steps with no retry config and no global retry config.

### Rollback Protocol

Used for compensating actions when a step fails.

1. A step fails (with retries exhausted or no retry config) and has `on_failure` set.
2. The orchestrator reads `yaml_content` from the `runbooks` table, parses the YAML.
3. Looks up `rollbacks[on_failure]` for the list of rollback steps.
4. Loads the member's data from `batch_members.data_json`.
5. Resolves templates in each rollback step's params using the member data.
6. Dispatches rollback steps to the worker (fire-and-forget, no status tracking).
7. The original step remains in `failed` status.

### Member Catch-Up Protocol

Handles late-joining members in active batches.

1. The orchestrator receives a `member-added` message.
2. Loads the runbook YAML from the database.
3. Finds phases that have already been dispatched or completed.
4. For each overdue phase:
   - Loads the new member's data from `batch_members.data_json`.
   - Resolves templates for each step in the phase.
   - Creates `step_executions` for the new member.
   - Dispatches the steps.
5. Future pending phases automatically include this member when the scheduler dispatches them normally.

### Member Removal Protocol

Handles members leaving an active batch.

1. The scheduler marks `batch_members.status = 'removed'` and publishes `member-removed`.
2. The orchestrator cancels all pending/dispatched step executions for the member.
3. Loads the runbook YAML and checks for `on_member_removed` steps.
4. If defined: loads member data, resolves templates, dispatches removal steps to the worker.

### Per-Member Progression (PhaseProgressionService)

The `PhaseProgressionService` centralizes all progression logic across the orchestrator:

- **`CheckMemberProgressionAsync`** -- After a step succeeds, walks that member's steps in step_index order and dispatches the next pending step. Each member progresses independently.
- **`HandleMemberFailureAsync`** -- After a step fails (with retries exhausted) or poll times out, marks the member as `failed` in `batch_members` and cancels all their non-terminal steps across ALL phases. Then checks if the phase is complete.
- **`CheckPhaseCompletionAsync`** -- When all steps in a phase are terminal: marks phase `Completed` if at least one member fully succeeded, or `Failed` if no member did.
- **`CheckBatchCompletionAsync`** -- When all phases are terminal: marks batch `Completed` if at least one phase completed, or `Failed` if none did.

### Version Transition Protocol

When a new runbook version is published while batches are active:

1. New `phase_executions` are created for the new version.
2. `overdue_behavior` (set at publish time, stored on the runbook record) controls past-due phases:
   - `rerun`: Re-execute phases that are already past due.
   - `ignore`: Skip past-due phases (mark as `skipped`). Applied once, tracked by `ignore_overdue_applied`.
3. `rerun_init`: Whether to re-run init steps with the new version.

---

## Security Model

### Authentication Inventory

| Component | Auth Method | Identity Type | Details |
|-----------|-------------|---------------|---------|
| Admin API | Entra ID JWT bearer | User (Entra ID) | `Microsoft.Identity.Web`; `Admin` role for writes, any authenticated user for reads |
| Admin CLI | Entra ID device code flow | User (Entra ID) | `Azure.Identity` with persistent MSAL token cache (`matoolkit-cli`) |
| Scheduler -> SQL | Managed identity | System-assigned MI | `Authentication=Active Directory Managed Identity` in connection string |
| Orchestrator -> SQL | Managed identity | System-assigned MI | Same as scheduler |
| Admin API -> SQL | Managed identity | System-assigned MI | Connection string stored in Key Vault, referenced via `@Microsoft.KeyVault(...)` |
| Scheduler -> Service Bus | Managed identity | System-assigned MI | `DefaultAzureCredential` via Azure Functions extension |
| Orchestrator -> Service Bus | Managed identity | System-assigned MI | `DefaultAzureCredential` via trigger binding + SDK |
| Cloud Worker -> Service Bus | Managed identity | System-assigned MI | `DefaultAzureCredential` via .NET SDK loaded in PowerShell |
| Cloud Worker -> Graph/EXO | Certificate auth | App registration (target tenant) | PFX from Key Vault; `Connect-MgGraph -Certificate` + `Connect-ExchangeOnline -Certificate` |
| KEDA -> Service Bus | Shared access key | `keda-monitor` auth rule | Listen-only; connection string stored in Key Vault |
| All components -> Key Vault | Managed identity | System-assigned MI | RBAC: Key Vault Secrets User |
| All components -> Storage | Managed identity | System-assigned MI | Identity-based connection (`AzureWebJobsStorage__accountName`) |

**Admin API Entra ID app registration requires:**
- App roles: `Admin` (full read/write) and `Reader` (read-only)
- Exposed API with Application ID URI (e.g., `api://{client-id}`)
- All endpoints use `AuthorizationLevel.Anonymous` (no function keys)

**User identity extraction** (`UserContextExtensions.GetUserIdentity()`) fallback chain: `preferred_username` claim -> `name` claim -> `oid` claim -> `"system"`.

### Managed Identity RBAC

| Component | RBAC Role | Scope | Purpose |
|-----------|-----------|-------|---------|
| Scheduler | Service Bus Data Sender | Service Bus Namespace | Publish orchestrator-events |
| Scheduler | Key Vault Secrets User | Key Vault | Read SQL connection string |
| Scheduler | Storage Blob Data Owner | Scheduler Storage Account | Functions runtime (blob leases, deployment) |
| Scheduler | Storage Table Data Contributor | Scheduler Storage Account | Functions runtime (timer state) |
| Orchestrator | Service Bus Data Sender | Service Bus Namespace | Publish worker-jobs, self-schedule retries |
| Orchestrator | Service Bus Data Receiver | Service Bus Namespace | Consume orchestrator-events, worker-results |
| Orchestrator | Key Vault Secrets User | Key Vault | Read SQL connection string |
| Orchestrator | Storage Blob Data Owner | Orchestrator Storage Account | Functions runtime |
| Orchestrator | Storage Table Data Contributor | Orchestrator Storage Account | Functions runtime |
| Admin API | Key Vault Secrets User | Key Vault | Read SQL connection string |
| Admin API | Storage Blob Data Owner | Admin API Storage Account | Functions runtime |
| Admin API | Storage Table Data Contributor | Admin API Storage Account | Functions runtime |
| Admin API | Service Bus Data Sender | Service Bus Namespace | Dispatch manual batch events |
| Cloud Worker | AcrPull | Container Registry | Pull container images |
| Cloud Worker | Key Vault Secrets User | Key Vault | Read PFX certificate, KEDA SB connection string |
| Cloud Worker | Service Bus Data Receiver | Service Bus Namespace | Consume worker-jobs |
| Cloud Worker | Service Bus Data Sender | Service Bus Namespace | Publish worker-results |

### SQL Database Access

All Function Apps connect to Azure SQL using managed identity (Entra-only authentication, no SQL passwords). Connection strings use `Authentication=Active Directory Managed Identity` and are stored in Key Vault, referenced by Function App settings via `@Microsoft.KeyVault(SecretUri=...)`. The cloud worker does not connect to SQL.

Required setup (run once per environment as the Entra ID admin):

```sql
CREATE USER [func-scheduler-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [func-scheduler-dev];
ALTER ROLE db_datawriter ADD MEMBER [func-scheduler-dev];

CREATE USER [func-orchestrator-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [func-orchestrator-dev];
ALTER ROLE db_datawriter ADD MEMBER [func-orchestrator-dev];

CREATE USER [matoolkit-admin-api-func] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [matoolkit-admin-api-func];
ALTER ROLE db_datawriter ADD MEMBER [matoolkit-admin-api-func];
```

### Secrets Management

All secrets are stored in Key Vault with RBAC authorization (no access policies). Components retrieve secrets via managed identity.

| Secret | Format | Consumer |
|--------|--------|----------|
| `scheduler-sql-connection-string` | `Server=tcp:...;Authentication=Active Directory Managed Identity;...` | Scheduler (via KV reference) |
| `orchestrator-sql-connection-string` | Same format | Orchestrator (via KV reference) |
| `admin-api-sql-connection-string` | Same format | Admin API (via KV reference) |
| `keda-sb-connection-string` | `Endpoint=sb://...;SharedAccessKeyName=keda-monitor;SharedAccessKey=...` | KEDA scaler (via ACA secret with `keyVaultUrl`) |
| Worker PFX certificate | Key Vault Certificate (retrieved via associated secret) | Cloud worker (at startup) |

**No passwords in connection strings.** SQL uses managed identity auth. Storage uses identity-based connections (`AzureWebJobsStorage__accountName`). Service Bus uses managed identity for all runtime operations. The only shared access key is the KEDA monitor rule.

### KEDA Shared Access Key

KEDA requires a Service Bus connection string to monitor subscription message counts for scaling decisions. Managed identity is not supported by the KEDA Service Bus scaler as of the current version.

- **Scope:** `keda-monitor` authorization rule on the `worker-jobs` topic only (not namespace-level)
- **Rights:** `Listen` only (read message count metrics, cannot send or manage)
- **Storage:** Connection string stored in Key Vault, sourced by ACA via `keyVaultUrl` with system-assigned managed identity
- **Risk:** Compromise of this key allows reading messages on the `worker-jobs` topic but not sending or modifying them

### External API Boundaries

Data flows leaving the VNet to external Microsoft services:

| Destination | Protocol | Consumer | Data Flowing Out |
|-------------|----------|----------|-----------------|
| Microsoft Graph API | HTTPS | Cloud worker | Entra ID user/group operations (create, modify, validate) |
| Exchange Online | HTTPS (PowerShell remoting) | Cloud worker | Mail user configuration, mailbox operations |
| Dataverse TDS endpoint | TDS (TCP 1433) | Scheduler | SQL queries against Dataverse tables |
| Databricks SQL Statements API | HTTPS | Scheduler | SQL queries via REST API |
| Application Insights | HTTPS | All components | Telemetry (traces, exceptions, metrics) |
| Container Registry | HTTPS | ACA | Container image pulls |

### Sensitive Data

| Data | Location | Protection |
|------|----------|------------|
| Member PII (names, UPNs, emails) | `batch_members.data_json`, `step_executions.params_json` | SQL PE (no public access), TDE at rest |
| Worker output data | `batch_members.worker_data_json`, `step_executions.result_json` | Same as above |
| Service Bus message payloads | `worker-jobs` / `worker-results` topics | SB PE (no public access), encryption at rest |
| PFX certificate private key | Key Vault | KV PE, RBAC, soft delete + purge protection |
| SQL connection strings | Key Vault | KV PE, RBAC, referenced via KV references (never in app settings directly) |
| JWT tokens (admin API calls) | In-transit only | HTTPS, short-lived tokens |
| MSAL token cache (CLI) | `~/.matoolkit/` on user's machine | OS credential store (Keychain/DPAPI/libsecret) |

### Attack Surface Summary

| Vector | Risk | Mitigation |
|--------|------|------------|
| Worker PFX certificate compromise | Full Graph + EXO access to target tenant | Key Vault with PE, RBAC, purge protection; certificate rotation |
| KEDA SAS key compromise | Read messages on `worker-jobs` topic | Listen-only scope, stored in KV, topic-level (not namespace) |
| Runbook query injection | Arbitrary SQL against Dataverse/Databricks | Queries are defined by admins at publish time (not user input); `Admin` role required |
| Member data exfiltration via runbook | Exfil PII through crafted function params | Runbook publish requires `Admin` role; functions execute in isolated worker |
| Admin API authorization bypass | Unauthorized batch creation or automation control | Entra ID JWT validation, app role enforcement, no function keys |
| Service Bus message spoofing | Inject fake worker results | Managed identity auth, no shared keys for send operations |

### Audit Trail

**Recorded:**
- `batches.created_by` — identity of manual batch creator
- `runbook_automation_settings.enabled_by` / `disabled_by` — who toggled automation
- Step execution progression (dispatched_at, completed_at, status transitions)
- Application Insights traces with `WorkerId`, `BatchId`, `StepExecutionId` dimensions

**Not recorded:**
- Individual runbook publish identity (no `published_by` column on `runbooks`)
- Member removal identity for manual batches (API removes, but `removed_at` has no `removed_by`)
- Detailed audit log of who accessed which batch/member data via read endpoints

---

## Component Deep Dives

### Scheduler Internals

#### Timer Lifecycle (Each 5-Minute Tick)

1. **Load active runbooks** -- Query `runbooks` table for `is_active = 1`.
2. **For each runbook** (isolated try/catch):
   a. Parse stored YAML into `RunbookDefinition`.
   b. Check automation settings -- skip if automation disabled.
   c. Execute data source query (Dataverse or Databricks).
   d. Group query rows by batch time.
   e. For each batch group: create new batch or diff members against existing batch.
   f. Evaluate pending phases for active batches where `due_at <= now`.
   g. Handle runbook version transitions for in-flight batches.
3. **Check polling steps** -- Across all runbooks, find `step_executions` and `init_executions` with `status = 'polling'` where `last_polled_at + poll_interval_sec <= now`. Send `poll-check` messages.

#### Key Services

| Service | Responsibility |
|---------|---------------|
| `DataSourceQueryService` | Routes to `DataverseQueryClient` or `DatabricksQueryClient` based on `data_source.type` |
| `DataverseQueryClient` | Executes SQL via Dataverse TDS endpoint using `SqlConnection` |
| `DatabricksQueryClient` | Executes SQL via Databricks SQL Statements REST API with `DefaultAzureCredential` |
| `RunbookParser` | YamlDotNet deserialization with `UnderscoredNamingConvention` and `IgnoreUnmatchedProperties` |
| `MemberDiffService` | Computes added/removed members between existing batch members and current query results |
| `PhaseEvaluator` | Parses offset strings, calculates `due_at`, creates phase execution records, handles version transitions |
| `MemberSynchronizer` | Refreshes member data for active members still present in query results |
| `ServiceBusPublisher` | Serializes and sends messages to `orchestrator-events` topic with `MessageType` application property |

#### Configuration

- `Scheduler__SqlConnectionString` -- SQL Server connection string
- `Scheduler__ServiceBusNamespace` -- Service Bus FQDN
- `Scheduler__OrchestratorTopicName` -- Topic name (default: `orchestrator-events`)
- `DATAVERSE_CONNECTION_STRING` -- Dataverse TDS endpoint (env var, referenced by runbook YAML)
- `DATABRICKS_CONNECTION_STRING` -- Databricks workspace URL (env var)
- `DATABRICKS_WAREHOUSE_ID` -- Databricks SQL warehouse ID (env var)

### Orchestrator Internals

#### Handler Architecture

The orchestrator has two Service Bus-triggered functions:

1. **`OrchestratorEventFunction`** -- Subscribes to `orchestrator-events/orchestrator`. Routes messages to handlers based on `MessageType` application property.
2. **`WorkerResultFunction`** -- Subscribes to `worker-results/orchestrator`. Routes all messages to `ResultProcessor`.

| Handler | Message Type | Responsibility |
|---------|-------------|---------------|
| `BatchInitHandler` | `batch-init` | Create init_executions on demand, dispatch sequentially |
| `PhaseDueHandler` | `phase-due` | Create step_executions per member, dispatch per member (parallel within step_index) |
| `MemberAddedHandler` | `member-added` | Create catch-up steps for overdue phases |
| `MemberRemovedHandler` | `member-removed` | Cancel pending steps, dispatch removal steps |
| `PollCheckHandler` | `poll-check` | Check timeout, re-dispatch or fail |
| `RetryCheckHandler` | `retry-check` | Verify step still pending, re-dispatch |
| `ResultProcessor` | Worker results | Update status, trigger retry/rollback, advance progression |

#### Execution Model

**Init steps** execute sequentially (one at a time) within a batch. The `BatchInitHandler` creates init_executions on demand (like `PhaseDueHandler` for step_executions), then dispatches the first pending step. `ResultProcessor` dispatches subsequent steps after each success. Version-aware idempotency checks `RunbookVersion` to avoid duplicates during version transitions.

**Phase steps** use per-member independent progression. Within a phase, steps at the same `step_index` are dispatched in parallel across all members. A member must complete step_index N before step_index N+1 is dispatched for that member. Different members can be at different step indices simultaneously.

```
Init Steps:        Step 0 --> Step 1 --> Step 2     (sequential, batch-wide)

Phase Steps:
  step_index=0:    [Member A] [Member B] [Member C]  (parallel)
                         |
  step_index=1:    [Member A] [Member B] [Member C]  (parallel, per-member progression)
                         |
  step_index=2:    [Member A] [Member B] [Member C]  (parallel)
```

#### Configuration

- `Orchestrator__SqlConnectionString` -- SQL Server connection string
- `Orchestrator__ServiceBusNamespace` -- Service Bus FQDN
- `Orchestrator__OrchestratorEventsTopicName` -- default: `orchestrator-events`
- `Orchestrator__OrchestratorSubscriptionName` -- default: `orchestrator`
- `Orchestrator__WorkerJobsTopicName` -- default: `worker-jobs`
- `Orchestrator__WorkerResultsTopicName` -- default: `worker-results`
- `Orchestrator__WorkerResultsSubscriptionName` -- default: `orchestrator`
- `ServiceBusConnection__fullyQualifiedNamespace` -- FQDN for trigger binding

### Admin API Internals

#### Layer Architecture

```
Function Layer          Service Layer           Data Layer
+------------------+   +------------------+   +------------------+
| *Function.cs     |   | *Service.cs      |   | *Repository.cs   |
| - HTTP binding   |   | - Business logic |   | - SQL via Dapper |
| - Auth attributes|   | - Orchestration  |   | - CRUD operations|
| - Input parsing  |   | - Validation     |   |                  |
| - Response format|   |                  |   |                  |
+------------------+   +------------------+   +------------------+
```

All services are registered as scoped (per-request). `IDbConnectionFactory` and `ServiceBusClient` are singletons.

#### Endpoints

| Method | Route | Description | Policy |
|--------|-------|-------------|--------|
| POST | `/api/runbooks` | Publish new runbook version | Admin |
| GET | `/api/runbooks` | List all active runbooks | Authenticated |
| GET | `/api/runbooks/{name}` | Get latest active version | Authenticated |
| GET | `/api/runbooks/{name}/versions` | List all versions | Authenticated |
| GET | `/api/runbooks/{name}/versions/{v}` | Get specific version | Authenticated |
| DELETE | `/api/runbooks/{name}/versions/{v}` | Deactivate version | Admin |
| GET | `/api/runbooks/{name}/automation` | Get automation status | Authenticated |
| PUT | `/api/runbooks/{name}/automation` | Enable/disable automation | Admin |
| POST | `/api/runbooks/{name}/query/preview` | Preview query results | Admin |
| GET | `/api/runbooks/{name}/template` | Download CSV template | Authenticated |
| GET | `/api/batches` | List batches (with filters) | Authenticated |
| GET | `/api/batches/{id}` | Get batch details | Authenticated |
| POST | `/api/batches` | Create batch from CSV | Admin |
| GET | `/api/batches/{id}/members` | List batch members | Authenticated |
| POST | `/api/batches/{id}/members` | Add members from CSV | Admin |
| DELETE | `/api/batches/{id}/members/{memberId}` | Remove member | Admin |
| GET | `/api/batches/{id}/phases` | List phase executions | Authenticated |
| GET | `/api/batches/{id}/steps` | List step executions | Authenticated |
| POST | `/api/batches/{id}/advance` | Advance manual batch | Admin |
| POST | `/api/batches/{id}/cancel` | Cancel batch | Admin |

#### Configuration

- `AdminApi__SqlConnectionString` -- SQL Server connection string
- `AdminApi__ServiceBusNamespace` -- Service Bus FQDN (optional, for manual batch dispatch)
- `AzureAd__Instance` -- `https://login.microsoftonline.com/`
- `AzureAd__TenantId` -- Entra ID tenant ID
- `AzureAd__ClientId` -- App registration client ID
- `AzureAd__Audience` -- API audience URI (e.g., `api://{client-id}`)

### Admin CLI Internals

#### Component Architecture

```
Program.cs (entry point)
  |-- BuildConfiguration()  -> env vars (MATOOLKIT_*) + ~/.matoolkit/config.json
  |-- AuthService           -> device code flow, persistent MSAL token cache
  |-- AdminApiClient        -> HTTP client, auto-injects bearer tokens
  +-- Commands/             -> one static class per command group
```

#### Configuration Hierarchy (highest priority first)

1. Command-line options (`--api-url`)
2. Environment variables (`MATOOLKIT_API_URL`, `MATOOLKIT_TENANT_ID`, `MATOOLKIT_CLIENT_ID`, `MATOOLKIT_API_SCOPE`)
3. Config file (`~/.matoolkit/config.json`)

#### Authentication Flow

1. User runs `matoolkit auth login`.
2. `AuthService` checks if `tenant-id` and `client-id` are configured.
3. `DeviceCodeCredential.GetTokenAsync()` checks for cached token.
4. If no cache: displays device code flow URL and code.
5. User completes browser flow; token cached to OS credential store (Keychain/DPAPI/libsecret).
6. Subsequent API calls: `AdminApiClient.GetConfiguredClientAsync()` calls `AuthService.GetAccessTokenAsync()`, which returns cached or refreshed token. `Authorization: Bearer <token>` header is attached automatically.

#### Commands

| Command | Description |
|---------|-------------|
| `runbook publish <file>` | Publish runbook from YAML file |
| `runbook list` | List all active runbooks |
| `runbook get <name>` | Get runbook definition |
| `runbook versions <name>` | List all versions |
| `runbook delete <name> <version>` | Deactivate version |
| `automation status <runbook>` | Get automation status |
| `automation enable <runbook>` | Enable automation |
| `automation disable <runbook>` | Disable automation |
| `query preview <runbook>` | Preview query results |
| `template download <runbook>` | Download CSV template |
| `batch list` | List batches |
| `batch get <id>` | Get batch details |
| `batch create <runbook> <file>` | Create batch from CSV |
| `batch advance <id>` | Advance manual batch |
| `batch cancel <id>` | Cancel batch |
| `batch members <id>` | List members |
| `batch add-members <id> <file>` | Add members from CSV |
| `batch remove-member <batch-id> <member-id>` | Remove member |
| `batch phases <id>` | List phase executions |
| `batch steps <id>` | List step executions |
| `auth login` | Sign in using device code flow |
| `auth status` | Show authentication status |
| `config show` | Show configuration |
| `config set <key> <value>` | Set configuration |
| `config path` | Show config file path |

### Cloud Worker Internals

#### Boot Sequence (worker.ps1)

The worker follows an 8-phase boot sequence:

1. Load configuration from environment variables (`config.ps1`)
2. Initialize logging (App Insights TelemetryClient + console fallback)
3. Authenticate to Azure via managed identity (`auth.ps1`)
4. Retrieve PFX certificate from Key Vault
5. Initialize Service Bus client (`servicebus.ps1`)
6. Create RunspacePool with per-runspace MgGraph + EXO sessions (`runspace-manager.ps1`)
7. Discover and load function modules (Standard + Custom)
8. Enter main job dispatch loop (`job-dispatcher.ps1`)

#### RunspacePool Architecture

Each runspace in the pool gets its own independent MgGraph and EXO session, avoiding thread-safety issues with shared connections. PFX certificate bytes are passed to runspaces (byte arrays serialize cleanly across boundaries) and reconstructed with `EphemeralKeySet` flag (avoids writing private keys to disk on Linux).

#### Standard Library Functions (14 total)

**Entra (EntraFunctions.ps1):**

| Function | Return Type | Description |
|----------|------------|-------------|
| `New-EntraUser` | Object | Create new Entra ID user |
| `Set-EntraUserUPN` | Boolean | Update user principal name |
| `Add-EntraGroupMember` | Boolean | Add user to group |
| `Remove-EntraGroupMember` | Boolean | Remove user from group |
| `New-EntraB2BInvitation` | Boolean | Invite existing internal user to B2B |
| `Convert-EntraB2BToInternal` | Boolean | Convert external to internal member |
| `Test-EntraAttributeMatch` | Object | Validate user attributes (supports multi-value) |
| `Test-EntraGroupMembership` | Object | Validate group membership |

**Exchange (ExchangeFunctions.ps1):**

| Function | Return Type | Description |
|----------|------------|-------------|
| `Add-ExchangeSecondaryEmail` | Boolean | Add secondary email address |
| `Set-ExchangePrimaryEmail` | Boolean | Set primary email address |
| `Set-ExchangeExternalAddress` | Boolean | Set external email address |
| `Set-ExchangeMailUserGuids` | Boolean | Set mail user GUIDs |
| `Test-ExchangeAttributeMatch` | Object | Validate Exchange attributes (supports multi-value) |
| `Test-ExchangeGroupMembership` | Object | Validate Exchange group membership |

Custom functions are discovered from the `CustomFunctions/` directory (volume mount or baked into image).

#### Throttle Handling

Retry with exponential backoff and jitter happens inside each runspace. Throttling exceptions (HTTP 429) respect `Retry-After` headers. Throttling is not reported as failure unless retries are exhausted.

#### Scale-to-Zero Pattern

- KEDA `azure-servicebus` scaler monitors the worker's subscription (polling interval: 30 seconds).
- When messages arrive: ACA scales from 0 to 1 replica.
- When idle (no messages and no active jobs for 300 seconds): worker exits gracefully.
- ACA scales back to 0 replicas.
- Max replicas: 1 (single worker instance per worker ID).

---

## Race Condition Safety

The orchestrator uses several guards to handle concurrent message processing:

| Scenario | Guard |
|----------|-------|
| Duplicate `phase-due` messages | `PhaseDueHandler` is idempotent: checks for existing steps before creation, dispatch targets only `pending` steps |
| Simultaneous results for same phase | SQL `WHERE status = 'dispatched'` guard on `SetCompletedAsync`/`SetFailedAsync`: only one writer succeeds |
| Result for already-terminal step | Terminal-state guard in `ResultProcessor`: ignores results for steps already in terminal status |
| Member failure + step success for same member | `SetFailedAsync` on member uses `WHERE status = 'active'` (idempotent). `SetCancelledAsync` on steps uses status guards (won't cancel already-succeeded steps) |
| `retry-check` arrives after step cancelled | `RetryCheckHandler` checks `status == pending` before dispatching |
| `SetRetryPendingAsync` race | Uses `WHERE status IN ('failed', 'poll_timeout')` guard: only transitions from expected states, returns false if already moved |

---

## Deployment Topology

### Two-Stage Bicep Deployment

**Stage 1 -- Shared Infrastructure:**

```bash
az deployment group create \
  --name shared-infra \
  --resource-group your-rg \
  --template-file infra/shared/deploy.bicep \
  --parameters infra/shared/deploy.parameters.json
```

Creates: VNet, subnets, private DNS zones, Service Bus namespace and topics, Key Vault, ACR, Log Analytics workspace, KV and SB private endpoints.

**Stage 2 -- Components** (consume subnet IDs from shared stage outputs):

```bash
# 2a. Scheduler + Orchestrator (creates SQL, storage, function apps, subscriptions, PEs)
az deployment group create \
  --name scheduler-orchestrator \
  --resource-group your-rg \
  --template-file infra/automation/scheduler-orchestrator/deploy.bicep \
  --parameters infra/automation/scheduler-orchestrator/deploy.parameters.json \
  --parameters sqlAdminPassword="..." \
  --parameters sqlEntraAdminObjectId="..."

# 2b. Admin API (after 2a -- needs SQL server FQDN + DB name)
az deployment group create \
  --name admin-api \
  --resource-group your-rg \
  --template-file infra/automation/admin-api/deploy.bicep \
  --parameters infra/automation/admin-api/deploy.parameters.json \
  --parameters entraIdTenantId="..." \
  --parameters entraIdClientId="..." \
  --parameters entraIdAudience="api://..."

# 2c. Cloud Worker
az deployment group create \
  --name cloud-worker \
  --resource-group your-rg \
  --template-file infra/automation/cloud-worker/deploy.bicep \
  --parameters infra/automation/cloud-worker/deploy.parameters.json
```

### CI/CD

Automated deployment is available via GitHub Actions:

- `.github/workflows/deploy-infra.yml` -- Infrastructure deployment (Bicep)
- `.github/workflows/deploy-apps.yml` -- Application code deployment

### Infrastructure Layout

```
infra/
  shared/
    deploy.bicep                         # Log Analytics, Service Bus, Key Vault, ACR,
    deploy.parameters.json               # VNet, subnets, DNS zones, KV/SB private endpoints
  automation/
    scheduler-orchestrator/
      deploy.bicep                       # SQL, storage, Function Apps, subscriptions,
      deploy.parameters.json             # SQL/storage private endpoints, RBAC
    admin-api/
      deploy.bicep                       # Storage, Function App, storage PEs, RBAC
      deploy.parameters.json
    cloud-worker/
      deploy.bicep                       # ACA environment, ACA app, KEDA scaler,
      deploy.parameters.json             # worker subscription, RBAC
```

### Technology Stack Summary

| Component | Language | Framework | Hosting | Data Access |
|-----------|----------|-----------|---------|-------------|
| Scheduler | C# | .NET 8, Azure Functions v4 (isolated) | Flex Consumption | Dapper + raw SQL |
| Orchestrator | C# | .NET 8, Azure Functions v4 (isolated) | Flex Consumption | Dapper + raw SQL |
| Admin API | C# | .NET 8, Azure Functions v4 (isolated) | Flex Consumption | Dapper + raw SQL |
| Admin CLI | C# | .NET, System.CommandLine, Spectre.Console | Global tool / self-contained | HTTP (Admin API) |
| Cloud Worker | PowerShell 7.4 | MgGraph, ExchangeOnlineManagement | Azure Container Apps | Service Bus .NET SDK |
| Database | -- | Azure SQL Serverless | GP_S_Gen5_1 | Entra-only auth |
| Messaging | -- | Azure Service Bus Standard | 3 topics | Managed identity |

### Shared Library

All .NET components (scheduler, orchestrator, admin API) reference `MaToolkit.Automation.Shared`, which provides:

- `IRunbookParser` -- YAML deserialization and validation
- `ITemplateResolver` -- `{{variable}}` resolution
- `IPhaseEvaluator` -- Offset parsing and `due_at` calculation
- `IDataSourceQueryService` -- Data source routing and query execution
- `IDatabricksQueryClient` / `IDataverseQueryClient` -- Data source clients
- `MemberDataSerializer` -- DataRow to JSON conversion with multi-valued column handling
- Repository interfaces -- Shared data access contracts
- YAML model classes -- `RunbookDefinition`, `PhaseDefinition`, `StepDefinition`, etc.
- DB model classes -- `RunbookRecord`, `BatchRecord`, `BatchMemberRecord`, etc.
