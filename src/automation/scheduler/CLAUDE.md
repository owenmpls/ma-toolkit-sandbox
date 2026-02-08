# CLAUDE.md -- Scheduler

## Project Overview

C# Azure Functions project using the **isolated worker model** (.NET 8, Functions v4). The scheduler is the timing and detection engine for the M&A Toolkit migration pipeline. It runs on a 5-minute timer, reads YAML runbook definitions from SQL, queries external data sources to discover migration members, detects batches, evaluates phase timing, and dispatches events to the orchestrator via Azure Service Bus. The orchestrator handles on-demand creation of init_executions and step_executions.

## Build and Run

```bash
# Build
dotnet build src/automation/scheduler/src/Scheduler.Functions/

# Run locally (requires Azure Functions Core Tools v4)
cd src/automation/scheduler/src/Scheduler.Functions && func start

# Publish (release build)
dotnet publish src/automation/scheduler/src/Scheduler.Functions/ -c Release -o src/automation/scheduler/src/Scheduler.Functions/publish
```

## Tests

```bash
# Run tests
dotnet test src/automation/scheduler/tests/Scheduler.Functions.Tests/

# Run with verbose output
dotnet test src/automation/scheduler/tests/Scheduler.Functions.Tests/ --verbosity normal
```

**Test coverage (5 tests):**
- `MemberSynchronizerTests` (5 tests) -- Data refresh on active members, status guard for removed/failed, skip members not in query results, multi-valued column handling

## Directory Structure

```
scheduler/
  Scheduler.sln
  docs/
    architecture.md              # System architecture and data flow
    deployment-guide.md          # Azure deployment and local dev setup
    runbook-format.md            # Complete YAML schema reference
    orchestrator-contract.md     # Service Bus messages, SQL tables, protocols
  tests/
    Scheduler.Functions.Tests/
      Scheduler.Functions.Tests.csproj
      Services/
        MemberSynchronizerTests.cs   # Member data refresh tests
  src/
    Scheduler.Functions/
      Scheduler.Functions.csproj
      Program.cs                 # DI registration, ServiceBusClient, all services
      host.json                  # Functions host config, App Insights sampling
      local.settings.json        # Local dev config (connection strings, env vars)
      Settings/
        SchedulerSettings.cs     # Options class: SqlConnectionString, ServiceBusNamespace, OrchestratorTopicName
      Functions/
        SchedulerTimerFunction.cs    # Main 5-min timer: runbook processing, batch detection, phase dispatch, poll checks
      Models/
        Db/
          RunbookRecord.cs           # runbooks table
          BatchRecord.cs             # batches table
          BatchMemberRecord.cs       # batch_members table
          PhaseExecutionRecord.cs    # phase_executions table
          StepExecutionRecord.cs     # step_executions table
          InitExecutionRecord.cs     # init_executions table
        Yaml/
          RunbookDefinition.cs       # Top-level YAML model (name, data_source, init, phases, rollbacks)
          DataSourceConfig.cs        # data_source section (type, connection, query, primary_key, batch_time_column)
          PhaseDefinition.cs         # Phase with name, offset, steps
          StepDefinition.cs          # Step with name, worker_id, function, params, on_failure, poll
          PollConfig.cs              # Poll interval and timeout
          RollbackSequence.cs        # Named rollback sequence (unused in code, dict used directly)
          MultiValuedColumnConfig.cs # Column name + format (semicolon_delimited, comma_delimited, json_array)
        Messages/
          BatchInitMessage.cs        # batch-init event
          PhaseDueMessage.cs         # phase-due event
          MemberAddedMessage.cs      # member-added event
          MemberRemovedMessage.cs    # member-removed event
          PollCheckMessage.cs        # poll-check event
      Services/
        DbConnectionFactory.cs          # Singleton: creates SqlConnection from config
        RunbookRepository.cs             # CRUD for runbooks table
        BatchRepository.cs               # CRUD for batches table
        MemberRepository.cs              # CRUD for batch_members table
        PhaseExecutionRepository.cs      # CRUD for phase_executions table
        StepExecutionRepository.cs       # CRUD for step_executions table
        InitExecutionRepository.cs       # CRUD for init_executions table
        DataverseQueryClient.cs          # Queries Dataverse via TDS endpoint (SqlConnection)
        DatabricksQueryClient.cs         # Queries Databricks via SQL Statements REST API
        DataSourceQueryService.cs        # Routes to Dataverse or Databricks based on config.type
        RunbookParser.cs                 # YamlDotNet deserialization + validation
        MemberDiffService.cs             # Computes added/removed members between query results and existing batch
        PhaseEvaluator.cs                # Parses offsets (T-5d), calculates due_at, creates phase execution records
        ServiceBusPublisher.cs           # Sends typed messages to orchestrator-events topic
```

## NuGet Packages

| Package | Version | Purpose |
|---|---|---|
| Microsoft.Azure.Functions.Worker | 2.0.0 | Isolated worker model runtime |
| Microsoft.Azure.Functions.Worker.Sdk | 2.0.0 | Build tooling for isolated worker |
| Microsoft.Azure.Functions.Worker.Extensions.Timer | 4.3.1 | Timer trigger binding |
| Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore | 2.0.0 | ASP.NET Core integration (required for FunctionsApplicationBuilder) |
| Microsoft.Extensions.Hosting | 9.0.0 | Generic host |
| Azure.Messaging.ServiceBus | 7.18.2 | Service Bus SDK for publishing messages |
| Azure.Identity | 1.13.1 | DefaultAzureCredential for managed identity auth |
| Microsoft.Data.SqlClient | 6.0.1 | SQL Server connectivity |
| Dapper | 2.1.35 | Lightweight ORM for all SQL operations |
| YamlDotNet | 16.2.1 | YAML deserialization for runbook definitions |
| Microsoft.ApplicationInsights.WorkerService | 2.22.0 | Application Insights telemetry |

## Key Architecture Decisions

- **Dapper for data access**: All SQL operations use Dapper with raw SQL. No Entity Framework. Repositories are scoped, DbConnectionFactory is singleton.
- **YamlDotNet for runbook parsing**: Runbooks are stored as raw YAML in the `runbooks.yaml_content` column and parsed on every timer tick. The parser uses `UnderscoredNamingConvention` and `IgnoreUnmatchedProperties`.
- **Member data storage**: Member data is stored as a JSON document (`data_json` column) on each `batch_members` row via `MemberDataSerializer`. Data is refreshed on every scheduler tick for active members still present in query results, so downstream phases always use the latest attribute values.
- **Template resolution**: Step params use `{{ColumnName}}` syntax resolved from member data dictionaries via the shared `ITemplateResolver`. Special vars: `{{_batch_id}}`, `{{_batch_start_time}}`.
- **Service Bus publishing**: All 5 message types (`batch-init`, `phase-due`, `member-added`, `member-removed`, `poll-check`) go to a single topic (`orchestrator-events`) with a `MessageType` application property for filtering.
- **Offset-based scheduling**: Phase offsets like `T-5d` are parsed to minutes. `due_at = batch_start_time - offset_minutes`. The timer evaluates `due_at <= now AND status = 'pending'`.
- **Version transitions**: When a new runbook version is published, existing active batches get new phase executions. The `overdue_behavior` setting (`rerun`/`ignore`) controls whether past-due phases are re-executed or skipped.
- **Multi-valued columns**: Semicolon-delimited, comma-delimited, or JSON array values are normalized to JSON arrays in the member data JSON document.
- **Polling**: Steps with `poll` config get `is_poll_step = 1` and interval/timeout stored in seconds. The scheduler checks `last_polled_at + interval <= now` and emits `poll-check` messages.

## SQL Tables

The core tables are: `runbooks`, `batches`, `batch_members`, `phase_executions`, `step_executions`, `init_executions`. See `docs/orchestrator-contract.md` for full column definitions.

Member data is stored as a JSON document in the `batch_members.data_json` column, refreshed on every scheduler tick for active members. The shared `MemberDataSerializer` handles `DataRow` → JSON conversion including multi-valued column handling.

## Configuration

Settings are bound via `IOptions<SchedulerSettings>` from the `Scheduler` config section:

- `Scheduler__SqlConnectionString` -- SQL Server connection string
- `Scheduler__ServiceBusNamespace` -- FQDN of the Service Bus namespace (e.g., `matoolkit-sb.servicebus.windows.net`)
- `Scheduler__OrchestratorTopicName` -- Topic name (default: `orchestrator-events`)

Data source connections are read from environment variables by name (configured in each runbook's `data_source.connection` field):

- `DATAVERSE_CONNECTION_STRING` -- Dataverse TDS endpoint connection string
- `DATABRICKS_CONNECTION_STRING` -- Databricks workspace URL
- `DATABRICKS_WAREHOUSE_ID` -- Databricks SQL warehouse ID
