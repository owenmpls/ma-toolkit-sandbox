# CLAUDE.md -- Runbook API

## Project Overview

C# Azure Functions project using the **isolated worker model** (.NET 8, Functions v4). The Runbook API provides a RESTful management interface for runbook definitions used by the M&A Toolkit migration pipeline. It handles publishing, versioning, retrieval, and deactivation of runbooks stored in SQL.

## Build and Run

```bash
# Build
dotnet build src/automation/runbook-api/src/RunbookApi.Functions/

# Run locally (requires Azure Functions Core Tools v4)
cd src/automation/runbook-api/src/RunbookApi.Functions && func start

# Publish (release build)
dotnet publish src/automation/runbook-api/src/RunbookApi.Functions/ -c Release -o src/automation/runbook-api/src/RunbookApi.Functions/publish
```

## Directory Structure

```
runbook-api/
  RunbookApi.sln
  CLAUDE.md
  src/
    RunbookApi.Functions/
      RunbookApi.Functions.csproj
      Program.cs                 # DI registration
      host.json                  # Functions host config, App Insights sampling
      local.settings.json        # Local dev config (connection strings)
      Settings/
        RunbookApiSettings.cs    # Options class: SqlConnectionString
      Functions/
        PublishRunbookFunction.cs    # POST /api/runbooks
        GetRunbookFunction.cs        # GET /api/runbooks/{name}, GET /api/runbooks/{name}/versions/{v}
        ListRunbooksFunction.cs      # GET /api/runbooks, GET /api/runbooks/{name}/versions
        DeleteRunbookFunction.cs     # DELETE /api/runbooks/{name}/versions/{v}
      Models/
        Db/
          RunbookRecord.cs           # runbooks table
        Yaml/
          RunbookDefinition.cs       # Top-level YAML model
          DataSourceConfig.cs        # data_source section
          PhaseDefinition.cs         # Phase definition
          StepDefinition.cs          # Step definition
          PollConfig.cs              # Poll interval and timeout
          RollbackSequence.cs        # Rollback sequence (unused, dict used)
          MultiValuedColumnConfig.cs # Multi-valued column config
        Requests/
          PublishRunbookRequest.cs   # Publish endpoint request body
      Services/
        DbConnectionFactory.cs       # Singleton: creates SqlConnection from config
        RunbookRepository.cs         # CRUD + query for runbooks table
        RunbookParser.cs             # YamlDotNet deserialization + validation
```

## API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/runbooks` | Publish new runbook version |
| GET | `/api/runbooks` | List all active runbooks |
| GET | `/api/runbooks/{name}` | Get latest active version of runbook |
| GET | `/api/runbooks/{name}/versions` | List all versions of a runbook |
| GET | `/api/runbooks/{name}/versions/{v}` | Get specific version |
| DELETE | `/api/runbooks/{name}/versions/{v}` | Deactivate (soft-delete) version |

## NuGet Packages

| Package | Version | Purpose |
|---|---|---|
| Microsoft.Azure.Functions.Worker | 2.0.0 | Isolated worker model runtime |
| Microsoft.Azure.Functions.Worker.Sdk | 2.0.0 | Build tooling for isolated worker |
| Microsoft.Azure.Functions.Worker.Extensions.Http | 3.2.0 | HTTP trigger binding |
| Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore | 2.0.0 | ASP.NET Core integration for HTTP |
| Microsoft.Extensions.Hosting | 9.0.0 | Generic host |
| Azure.Identity | 1.13.1 | DefaultAzureCredential for managed identity auth |
| Microsoft.Data.SqlClient | 6.0.1 | SQL Server connectivity |
| Dapper | 2.1.35 | Lightweight ORM for all SQL operations |
| YamlDotNet | 16.2.1 | YAML deserialization for runbook validation |
| Microsoft.ApplicationInsights.WorkerService | 2.22.0 | Application Insights telemetry |

## Key Architecture Decisions

- **Separated from Scheduler**: This API was extracted from the scheduler to follow single responsibility principle. The scheduler handles timing/detection (timer-triggered), while this API handles runbook management (HTTP-triggered).
- **Dapper for data access**: All SQL operations use Dapper with raw SQL. No Entity Framework.
- **YamlDotNet for runbook parsing**: Runbooks are validated on publish. The parser uses `UnderscoredNamingConvention` and `IgnoreUnmatchedProperties`.
- **Soft-delete pattern**: Runbooks are deactivated rather than deleted, preserving history and preventing data loss.
- **Version management**: Publishing a new version automatically deactivates all previous versions. Dynamic table names include version number (`runbook_{name}_v{version}`).

## Configuration

Settings are bound via `IOptions<RunbookApiSettings>` from the `RunbookApi` config section:

- `RunbookApi__SqlConnectionString` -- SQL Server connection string

## SQL Tables

This API reads and writes to the `runbooks` table:

| Column | Type | Description |
|--------|------|-------------|
| id | INT | Primary key |
| name | NVARCHAR(255) | Runbook name |
| version | INT | Version number |
| yaml_content | NVARCHAR(MAX) | Raw YAML content |
| data_table_name | NVARCHAR(255) | Generated table name |
| is_active | BIT | Active flag |
| overdue_behavior | NVARCHAR(50) | 'rerun' or 'ignore' |
| ignore_overdue_applied | BIT | Flag for version transition handling |
| rerun_init | BIT | Re-run init on version transition |
| created_at | DATETIME2 | Creation timestamp |

## Infrastructure

Deploy using the Bicep template at `infra/automation/runbook-api/deploy.bicep`:

```bash
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/runbook-api/deploy.bicep \
  --parameters infra/automation/runbook-api/deploy.parameters.json \
  --parameters sqlConnectionString="your-connection-string"
```
