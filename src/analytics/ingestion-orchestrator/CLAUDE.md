# CLAUDE.md -- Ingestion Orchestrator

## Project Overview

C# Azure Functions project using the **isolated worker model** (.NET 8, Functions v4). Replaces Azure Data Factory for scheduling and dispatching analytics ingestion ACA container jobs (graph-ingest, exo-ingest, spo-ingest).

Configuration lives in JSON files deployed with the app (`config/ingestion/`). Run history is written to ADLS as JSONL, queryable via DLT in Databricks. Overlap protection uses in-memory tracking.

## Build and Run

```bash
# Build
dotnet build src/analytics/ingestion-orchestrator/

# Run locally (requires Azure Functions Core Tools v4)
cd src/analytics/ingestion-orchestrator/src/IngestionOrchestrator.Functions && func start
```

## Tests

```bash
# Run tests
dotnet test src/analytics/ingestion-orchestrator/

# Run with verbose output
dotnet test src/analytics/ingestion-orchestrator/ --verbosity normal
```

## Directory Structure

```
ingestion-orchestrator/
  IngestionOrchestrator.sln
  CLAUDE.md
  src/
    IngestionOrchestrator.Functions/
      IngestionOrchestrator.Functions.csproj
      Program.cs
      host.json
      local.settings.json
      Settings/
        IngestionSettings.cs
      Models/
        EntityType.cs
        TenantConfig.cs
        JobDefinition.cs
        StorageConfig.cs
        RunRecord.cs
        TaskRecord.cs
      Services/
        ConfigLoader.cs          -- Loads + validates all 4 JSON config files
        EntityResolver.cs        -- EntitySelector → resolved entity list
        TenantResolver.cs        -- TenantSelector → resolved tenant list
      Functions/
        (Phase 2: IngestionTimerFunction, ManualRunFunction, PreviewFunction)
  tests/
    IngestionOrchestrator.Functions.Tests/
      EntityResolverTests.cs     -- 9 tests: tier selection, include/exclude, ordering
      TenantResolverTests.cs     -- 6 tests: all/specific/all_except modes, disabled filtering
```

## Configuration

Config files live in `config/ingestion/` and are deployed with the app via `CopyToOutputDirectory`:

- `entity-registry.json` -- Entity type catalog (name, tier 1-5, container)
- `tenants.json` -- Tenant identity + credential references
- `jobs.json` -- Job definitions with cron + entity/tenant selectors
- `storage.json` -- Deployment-wide storage target (account URL, auth method)

## Key Architecture Decisions

- **No SQL database**: All config is JSON files. Run history goes to ADLS. Overlap protection is in-memory.
- **Entity selection algebra**: `(entities in included tiers) ∪ include_entities − exclude_entities`
- **Tenant config as env vars**: Orchestrator passes all tenant config to containers as environment variables. No KV tenant registry.
- **Configurable storage auth**: Supports managed identity (default) and service principal (for customer-managed ADLS or Fabric/OneLake).
- **DLT is independent**: DLT pipelines run on their own Databricks Workflows schedule, not managed by this orchestrator.
