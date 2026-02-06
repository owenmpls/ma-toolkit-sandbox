# CLAUDE.md -- Admin API

## Project Overview

C# Azure Functions project using the **isolated worker model** (.NET 8, Functions v4). The Admin API provides RESTful endpoints for managing runbook definitions, controlling automation, previewing queries, and managing batches including manual batch creation from CSV uploads.

## Build and Run

```bash
# Build
dotnet build src/automation/admin-api/src/AdminApi.Functions/

# Run locally (requires Azure Functions Core Tools v4)
cd src/automation/admin-api/src/AdminApi.Functions && func start

# Publish (release build)
dotnet publish src/automation/admin-api/src/AdminApi.Functions/ -c Release -o src/automation/admin-api/src/AdminApi.Functions/publish
```

## Tests

```bash
# Run tests
dotnet test src/automation/admin-api/tests/AdminApi.Functions.Tests/

# Run with verbose output
dotnet test src/automation/admin-api/tests/AdminApi.Functions.Tests/ --verbosity normal
```

**Test coverage (58 tests):**
- `CsvUploadServiceTests` (20 tests) – CSV parsing, primary key validation, duplicate detection, quoted values
- `CsvTemplateServiceTests` (19 tests) – Template generation, column extraction, multi-valued formats
- `QueryPreviewServiceTests` (11 tests) – Query execution, sample rows, batch grouping
- `ManualBatchServiceTests` (8 tests) – Init dispatch, phase advancement, error handling

## Directory Structure

```
admin-api/
  AdminApi.sln
  CLAUDE.md
  src/
    AdminApi.Functions/
      AdminApi.Functions.csproj
      Program.cs                     # DI registration, ServiceBusClient, all services
      host.json                      # Functions host config, App Insights sampling
      local.settings.json            # Local dev config (connection strings)
      Settings/
        AdminApiSettings.cs          # Options class: SqlConnectionString, ServiceBusNamespace
      Functions/
        # Runbook management
        PublishRunbookFunction.cs    # POST /api/runbooks - publish new version
        GetRunbookFunction.cs        # GET /api/runbooks/{name} - get latest version
        ListRunbooksFunction.cs      # GET /api/runbooks - list all active
        DeleteRunbookFunction.cs     # DELETE /api/runbooks/{name}/versions/{v}
        # Automation control
        AutomationSettingsFunction.cs # GET/PUT /api/runbooks/{name}/automation
        # Query preview
        QueryPreviewFunction.cs      # POST /api/runbooks/{name}/query/preview
        # CSV template
        CsvTemplateFunction.cs       # GET /api/runbooks/{name}/template
        # Batch management
        BatchManagementFunction.cs   # Batch CRUD, advance, cancel
        MemberManagementFunction.cs  # Member list, add, remove
      Models/
        Requests/
          PublishRunbookRequest.cs   # Runbook name + YAML content
          SetAutomationRequest.cs    # Enable/disable + user identifier
          CreateBatchRequest.cs      # Runbook name + CSV content
        Responses/
          QueryPreviewResponse.cs    # Query results preview
      Services/
        DbConnectionFactory.cs       # Singleton: creates SqlConnection from config
        RunbookRepository.cs         # CRUD for runbooks table
        QueryPreviewService.cs       # Execute queries without side effects
        CsvTemplateService.cs        # Generate CSV templates from runbook
        CsvUploadService.cs          # Parse and validate CSV uploads
        ManualBatchService.cs        # Create and advance manual batches
        Repositories/
          AutomationSettingsRepository.cs  # CRUD for runbook_automation_settings
          BatchRepository.cs              # CRUD for batches table
          MemberRepository.cs             # CRUD for batch_members table
          PhaseExecutionRepository.cs     # CRUD for phase_executions table
          StepExecutionRepository.cs      # CRUD for step_executions table
          InitExecutionRepository.cs      # CRUD for init_executions table
  tests/
    AdminApi.Functions.Tests/
      AdminApi.Functions.Tests.csproj
      Services/
        CsvUploadServiceTests.cs         # CSV parsing tests
        CsvTemplateServiceTests.cs       # Template generation tests
        QueryPreviewServiceTests.cs      # Query preview tests
        ManualBatchServiceTests.cs       # Batch management tests
```

## API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/runbooks` | Publish new runbook version |
| GET | `/api/runbooks` | List all active runbooks |
| GET | `/api/runbooks/{name}` | Get latest active version |
| GET | `/api/runbooks/{name}/versions` | List all versions |
| GET | `/api/runbooks/{name}/versions/{v}` | Get specific version |
| DELETE | `/api/runbooks/{name}/versions/{v}` | Deactivate version |
| GET | `/api/runbooks/{name}/automation` | Get automation status |
| PUT | `/api/runbooks/{name}/automation` | Enable/disable automation |
| POST | `/api/runbooks/{name}/query/preview` | Preview query results |
| GET | `/api/runbooks/{name}/template` | Download CSV template |
| GET | `/api/batches` | List batches (with filters) |
| GET | `/api/batches/{id}` | Get batch details |
| POST | `/api/batches` | Create batch from CSV |
| GET | `/api/batches/{id}/members` | List batch members |
| POST | `/api/batches/{id}/members` | Add members from CSV |
| DELETE | `/api/batches/{id}/members/{memberId}` | Remove member |
| GET | `/api/batches/{id}/phases` | List phase executions |
| GET | `/api/batches/{id}/steps` | List step executions |
| POST | `/api/batches/{id}/advance` | Advance manual batch |
| POST | `/api/batches/{id}/cancel` | Cancel batch |

## NuGet Packages

| Package | Version | Purpose |
|---|---|---|
| Microsoft.Azure.Functions.Worker | 2.0.0 | Isolated worker model runtime |
| Microsoft.Azure.Functions.Worker.Sdk | 2.0.0 | Build tooling for isolated worker |
| Microsoft.Azure.Functions.Worker.Extensions.Http | 3.2.0 | HTTP trigger binding |
| Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore | 2.0.0 | ASP.NET Core integration |
| Microsoft.Extensions.Hosting | 9.0.0 | Generic host |
| Azure.Messaging.ServiceBus | 7.18.2 | Service Bus SDK for dispatching events |
| Azure.Identity | 1.13.1 | DefaultAzureCredential for managed identity auth |
| Microsoft.ApplicationInsights.WorkerService | 2.22.0 | Application Insights telemetry |

**Shared library (MaToolkit.Automation.Shared):**
| Package | Purpose |
|---|---|
| Microsoft.Data.SqlClient | SQL Server connectivity |
| Dapper | Lightweight ORM for all SQL operations |
| YamlDotNet | YAML deserialization for runbook definitions |

## Key Architecture Decisions

### Runbook Management
- **Versioning**: Each publish creates a new version. Only one version is active at a time per runbook name.
- **YAML storage**: Raw YAML stored in `runbooks.yaml_content` column, parsed on demand.
- **Validation**: Publish validates YAML syntax and required fields before storing.

### Automation Control
- **Separate table**: `runbook_automation_settings` tracks enable/disable state per runbook.
- **Scheduler integration**: Scheduler checks automation status before executing data source queries.
- **Existing batches continue**: Disabling automation only stops new batch creation, not processing of existing batches.

### Query Preview
- **No side effects**: Executes the runbook's data source query and returns results without creating batches.
- **Batch grouping**: Shows how members would be grouped into batches by the `batch_time_column`.

### CSV Template
- **Column discovery**: Parses runbook query to extract expected columns.
- **Multi-valued formats**: Sample row shows semicolon_delimited, comma_delimited, or json_array formats.

### Manual Batches
- **No time-based scheduling**: Manual batches have `batch_start_time = NULL` and `due_at = NULL`.
- **Explicit advancement**: Admin calls `/advance` to run init steps, then each phase in order.
- **Same orchestrator processing**: Once dispatched, phases execute through the normal orchestrator flow.

### CSV Upload Flow
1. Parse CSV, validate primary key column exists, check for duplicates
2. Create batch with `is_manual = 1`, `batch_start_time = NULL`
3. Create/update dynamic table with CSV data
4. Insert batch_members for each row
5. Create phase_executions with `status = 'pending'`, `due_at = NULL`
6. If init steps exist, create init_executions

### Manual Batch Advancement Flow
1. Validate batch is manual (`is_manual = 1`)
2. Check current state:
   - If init steps pending → dispatch init steps
   - If init in progress → return error (wait for completion)
   - If all phases complete → return success
3. Find next pending phase, verify previous phases complete
4. Dispatch phase to orchestrator via Service Bus
5. Update phase status and batch.current_phase

## Configuration

Settings are bound via `IOptions<AdminApiSettings>` from the `AdminApi` config section:

- `AdminApi__SqlConnectionString` – SQL Server connection string
- `AdminApi__ServiceBusNamespace` – FQDN of the Service Bus namespace (optional for manual batch advancement)

Data source connections are read from environment variables by name (configured in each runbook's `data_source.connection` field):

- `DATAVERSE_CONNECTION_STRING` – Dataverse TDS endpoint connection string
- `DATABRICKS_CONNECTION_STRING` – Databricks workspace URL
- `DATABRICKS_WAREHOUSE_ID` – Databricks SQL warehouse ID

## SQL Tables

**Core tables** (shared with scheduler/orchestrator):
- `runbooks` – Runbook definitions with YAML content
- `batches` – Batch records (includes `is_manual`, `created_by`, `current_phase` for manual batches)
- `batch_members` – Member records for each batch
- `phase_executions` – Phase status tracking
- `step_executions` – Step execution records
- `init_executions` – Init step execution records

**Admin-specific table:**
- `runbook_automation_settings` – Automation enable/disable per runbook

**Dynamic tables** (created by shared library):
- `runbook_{name}_v{version}` – Member data from data source queries or CSV uploads

## Infrastructure

Deploy via Bicep template at `infra/automation/admin-api/deploy.bicep`:

```bash
az deployment group create \
  --resource-group your-rg \
  --template-file infra/automation/admin-api/deploy.bicep \
  --parameters infra/automation/admin-api/deploy.parameters.json \
  --parameters sqlConnectionString="your-connection-string"
```

Creates:
- Flex Consumption Function App (.NET 8 isolated)
- Storage Account (for Functions runtime)
- Application Insights
- RBAC: Key Vault Secrets User on vault (for SQL connection string)
- Optional: Service Bus Data Sender role for manual batch advancement
