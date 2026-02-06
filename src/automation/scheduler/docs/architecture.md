# Scheduler Architecture

## System Context

The scheduler is a C# Azure Functions application (isolated worker model, .NET 8) that drives the M&A Toolkit migration pipeline. It runs on a 5-minute timer and is responsible for:

- Reading YAML runbook definitions from SQL
- Querying external data sources (Dataverse via TDS endpoint, Databricks via SQL Statements API) to discover migration members
- Detecting new batches and tracking member additions/removals
- Evaluating phase timing offsets to determine when migration steps become due
- Pre-creating step execution records with resolved template parameters
- Dispatching event messages to the orchestrator via Azure Service Bus

The scheduler does not execute migration work itself. It publishes events to a Service Bus topic (`orchestrator-events`), and the orchestrator (a separate component, not yet built) consumes those events, dispatches jobs to the cloud-worker, and manages execution state.

```
                          +-------------------+
                          |   Data Sources    |
                          | Dataverse / Bricks|
                          +--------+----------+
                                   |
                                   | query results
                                   v
+----------+    YAML    +-------------------+   Service Bus    +----------------+
|  SQL DB  | ---------> |    Scheduler      | ---------------> | orchestrator-  |
| runbooks |            | (Azure Function)  |   topic msgs     |   events       |
| batches  | <--------- |  5-min timer      |                  |   topic        |
| phases   |   upsert   +-------------------+                  +-------+--------+
| steps    |                                                           |
| members  |                                                           v
| dynamic  |                                                   +----------------+
| tables   |                                                   | Orchestrator   |
+----------+                                                   | (not yet built)|
                                                               +-------+--------+
                                                                       |
                                                                       v
                                                               +----------------+
                                                               | cloud-worker|
                                                               | (ACA container)|
                                                               +----------------+
```

## Scheduler Lifecycle

The scheduler exposes a single timer-triggered Azure Function (`SchedulerTimer`) that fires every 5 minutes via the CRON expression `0 */5 * * * *`. Each invocation follows this sequence:

1. **Load active runbooks** -- Query `runbooks` table for all records where `is_active = 1`.
2. **For each runbook**, process in a try/catch so one failure does not block others:
   a. Parse the stored YAML content into a `RunbookDefinition`.
   b. Execute the data source query (Dataverse or Databricks).
   c. Ensure the dynamic data table exists in SQL with the correct columns.
   d. Upsert query results into the dynamic table (MERGE by `_member_key`).
   e. Group query rows by batch time (either a column value or rounded-to-5-min UTC for `immediate` runbooks).
   f. For each batch group, either create a new batch or diff members against the existing batch.
   g. Evaluate all pending phases for active batches whose `due_at <= now`.
   h. Handle runbook version transitions for in-flight batches.
3. **Check polling steps** -- Across all runbooks, find `step_executions` and `init_executions` with `status = 'polling'` whose next poll interval has elapsed, and dispatch `poll-check` messages.
4. Log completion.

## Data Flow

### New Batch Discovery

```
Data Source Query
       |
       v
DataTable (rows with primary_key + batch_time_column)
       |
       v
GroupByBatchTime()
       |
       +-- batch not in DB --> CreateNewBatchAsync()
       |       |
       |       +-- INSERT batch (status: detected)
       |       +-- INSERT batch_members
       |       +-- INSERT phase_executions (with calculated due_at)
       |       +-- INSERT init_executions (if runbook has init steps)
       |       +-- PUBLISH batch-init message (if init steps exist)
       |       +-- Or set batch status to "active" (if no init steps)
       |
       +-- batch exists, not completed/failed --> ProcessExistingBatchAsync()
               |
               +-- MemberDiffService.ComputeDiff()
               +-- For added members: INSERT + PUBLISH member-added
               +-- For removed members: UPDATE status + PUBLISH member-removed
```

### Phase Evaluation

```
GetActiveByRunbookAsync() --> list of active batches
       |
       v
For each batch:
       |
       +-- GetPendingDueAsync(batchId, now) --> phases where due_at <= now AND status = pending
       |
       v
For each due phase:
       |
       +-- Load active members for batch
       +-- Load member data from dynamic table
       +-- For each member x each step in the phase:
       |       +-- TemplateResolver resolves {{ColumnName}} in params
       |       +-- INSERT step_execution with resolved params_json
       |
       +-- PUBLISH phase-due message
       +-- Mark phase_execution as dispatched
```

### Dynamic Table Lifecycle

When the scheduler first encounters a runbook, `DynamicTableManager.EnsureTableAsync` creates a SQL table named `runbook_{sanitized_name}_v{version}` with:

- System columns: `_row_id`, `_member_key`, `_batch_time`, `_first_seen_at`, `_last_seen_at`, `_is_current`
- One `NVARCHAR(MAX)` column per column returned by the data source query
- A unique constraint on `_member_key`

On each timer tick, `UpsertDataAsync` performs a SQL `MERGE` for each row. Rows no longer present in query results have `_is_current` set to `0`. Multi-valued columns (semicolon-delimited, comma-delimited, or JSON array) are normalized to JSON arrays during upsert.

## Component Interactions

### Azure Functions

| Function | Trigger | Purpose |
|---|---|---|
| `SchedulerTimer` | Timer (5 min CRON) | Main processing loop: runbook evaluation, batch detection, phase dispatch, poll checks |
| `PublishRunbook` | HTTP POST | Accepts a runbook name + YAML content, validates, versions, and inserts into SQL |

### Repositories (Dapper, scoped lifetime)

| Repository | Table | Key Operations |
|---|---|---|
| `RunbookRepository` | `runbooks` | Get active, insert, deactivate old versions, set ignore_overdue_applied |
| `BatchRepository` | `batches` | Get by runbook+time, get active by runbook, insert, update status |
| `MemberRepository` | `batch_members` | Get by batch, get active by batch, insert, mark removed, check active membership |
| `PhaseExecutionRepository` | `phase_executions` | Get by batch, get pending due, insert, set dispatched, update status |
| `StepExecutionRepository` | `step_executions` | Get by phase, get polling due, insert, update last polled |
| `InitExecutionRepository` | `init_executions` | Get by batch, get polling due, insert, update last polled |

### Services (scoped lifetime)

| Service | Responsibility |
|---|---|
| `DataSourceQueryService` | Routes to `DataverseQueryClient` or `DatabricksQueryClient` based on `data_source.type` |
| `DataverseQueryClient` | Executes SQL via Dataverse TDS endpoint using `SqlConnection` |
| `DatabricksQueryClient` | Executes SQL via Databricks SQL Statements REST API with `DefaultAzureCredential` |
| `DynamicTableManager` | Creates and upserts into per-runbook-version dynamic tables |
| `RunbookParser` | Deserializes YAML with YamlDotNet, validates structure |
| `MemberDiffService` | Computes added/removed members between existing batch members and current query results |
| `PhaseEvaluator` | Parses offset strings (T-5d), calculates due_at times, creates phase execution records, handles version transitions |
| `TemplateResolver` | Resolves `{{ColumnName}}` and `{{_batch_id}}` / `{{_batch_start_time}}` in step params |
| `ServiceBusPublisher` | Serializes and sends messages to the `orchestrator-events` topic with `MessageType` application property |

### Singletons

| Component | Lifetime | Purpose |
|---|---|---|
| `DbConnectionFactory` | Singleton | Creates `SqlConnection` instances from configured connection string |
| `ServiceBusClient` | Singleton | Azure SDK client authenticated via `DefaultAzureCredential` |

## Key Design Decisions

### Queue-Based Decoupling with Service Bus

The scheduler never talks to the worker directly. It publishes typed events (`batch-init`, `phase-due`, `member-added`, `member-removed`, `poll-check`) to a Service Bus topic. The orchestrator subscribes and is responsible for dispatching individual jobs to workers. This decoupling means:

- The scheduler can run independently of whether the orchestrator or workers are online.
- Messages survive component restarts (Service Bus persistence).
- The orchestrator can be developed and deployed separately.

### Dynamic Tables for Member Data

Rather than storing member data in a fixed schema, the scheduler creates a SQL table per runbook version whose columns match the data source query output. This allows runbooks to query arbitrary columns and reference them in `{{template}}` expressions without schema changes.

### Template Resolution at Phase Dispatch Time

Step parameters are resolved from member data at the moment a phase becomes due, not when the batch is first detected. This means if member data changes between batch detection and phase execution, the step gets the latest data. The `_is_current = 1` filter ensures only the most recent row for each member is used.

### RunbookVersion Transitions

When a new version of a runbook is published, existing active batches need to transition. The scheduler detects this when a batch has phase executions only from older versions and creates new phase executions for the current version. The `overdue_behavior` setting (`rerun` or `ignore`) controls whether phases whose `due_at` has already passed get status `pending` (will execute) or `skipped`.

### Offset-Based Phase Scheduling

Phase timing is expressed as offsets from `batch_start_time` using `T-` notation (e.g., `T-5d` means 5 days before). Offsets are converted to minutes and stored as `offset_minutes`. The `due_at` is calculated as `batch_start_time - offset_minutes`. This means `T-5d` fires 5 days before the batch time, and `T-0` fires at exactly the batch time.

### Immediate vs. Scheduled Batches

Runbooks can use `batch_time: immediate` instead of a `batch_time_column`. In immediate mode, the batch time is set to the current UTC time rounded to the nearest 5-minute interval. Members already in an active batch for the same runbook are filtered out to prevent duplicates.
