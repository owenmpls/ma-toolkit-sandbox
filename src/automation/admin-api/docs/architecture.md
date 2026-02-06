# Admin API Architecture

## Overview

The Admin API is a C# Azure Functions project (isolated worker, .NET 8) that serves as the control plane for the M&A Toolkit migration pipeline. It exposes 20 RESTful endpoints for managing runbook definitions, controlling automation, creating batches, and tracking execution.

## System Context

```
                                +-----------------+
                                |   Entra ID      |
                                | (JWT validation) |
                                +--------+--------+
                                         |
    +----------+    HTTPS/JWT    +-------v--------+    Service Bus    +---------------+
    | Admin CLI|--------------->|   Admin API     |---------------->| Orchestrator  |
    | (matoolkit)               | (Azure Functions)|                 | (Azure Funcs) |
    +----------+                +-------+--------+                  +-------+-------+
                                        |                                   |
                                  SQL Server                          Service Bus
                                        |                                   |
                                +-------v--------+                  +-------v-------+
                                |   SQL Database  |                  | Cloud Worker  |
                                | (runbooks,      |                  | (PowerShell,  |
                                |  batches, etc.) |                  |  ACA)         |
                                +----------------+                  +---------------+
```

The Admin API is the only user-facing component in the pipeline. All other components (scheduler, orchestrator, cloud worker) are internal and communicate via Service Bus.

## Authentication & Authorization

### Flow

1. Client obtains a JWT token from Entra ID (device code flow for CLI, client credentials for service-to-service)
2. Client sends request with `Authorization: Bearer <token>` header
3. Azure Functions middleware validates the JWT via `Microsoft.Identity.Web`
4. `[Authorize]` attributes on function methods enforce role-based policies
5. `UserContextExtensions.GetUserIdentity()` extracts the caller's identity from claims

### Entra ID App Registration

The API requires an Entra ID app registration with:

- **App roles** defined in the manifest:
  - `Admin` — full read/write access (publish runbooks, create batches, modify automation)
  - `Reader` — read-only access (list runbooks, view batches, download templates)
- **Expose an API** — set an Application ID URI (e.g., `api://{client-id}`)
- **API permissions** — no downstream APIs required

### Policy Enforcement

```
HTTP Request
    |
    v
[Azure Functions HTTP Pipeline]
    |
    v
[JWT Bearer Middleware] -- validates token signature, issuer, audience
    |
    v
[Authorization Middleware] -- checks [Authorize] attribute + policy
    |                          AdminPolicy: requires "Admin" role claim
    |                          AuthenticatedPolicy: requires valid identity
    v
[Function Method] -- req.GetUserIdentity() extracts caller
```

All endpoints use `AuthorizationLevel.Anonymous` — there are no function keys. Authentication is handled entirely by the JWT middleware.

### User Identity Extraction

`UserContextExtensions.GetUserIdentity()` uses a fallback chain to determine the caller:

1. `preferred_username` claim (e.g., `user@contoso.com`) — standard for interactive users
2. `name` claim — display name fallback
3. `oid` claim — object ID fallback for service principals
4. `"system"` — fallback when unauthenticated (shouldn't happen with auth enforced)

This identity is recorded in:
- `AutomationSettingsFunction` — who enabled/disabled automation
- `BatchManagementFunction` — who created a batch (`created_by` column)

## Request Processing

### Layer Architecture

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

- **Function Layer** — 9 function classes with `[HttpTrigger]` bindings. Handles HTTP parsing, authorization, and response formatting. Thin layer that delegates to services.
- **Service Layer** — Business logic for CSV parsing, template generation, query preview, and batch management. Stateless, injected via DI.
- **Data Layer** — Repository classes using Dapper for SQL operations. One repository per table.

### Dependency Injection

All services are registered in `Program.cs` as scoped (per-request):

```
Program.cs
  ├── Authentication (JWT bearer via Microsoft.Identity.Web)
  ├── Authorization (AdminPolicy, AuthenticatedPolicy)
  ├── Settings (IOptions<AdminApiSettings>)
  ├── Infrastructure
  │   ├── IDbConnectionFactory (singleton)
  │   └── ServiceBusClient? (singleton, optional)
  ├── Shared Services
  │   ├── IDatabricksQueryClient
  │   ├── IDataverseQueryClient
  │   ├── IDataSourceQueryService
  │   ├── IDynamicTableManager
  │   ├── IPhaseEvaluator
  │   └── IRunbookParser
  ├── Repositories
  │   ├── IRunbookRepository
  │   ├── IAutomationSettingsRepository
  │   ├── IBatchRepository
  │   ├── IMemberRepository
  │   ├── IPhaseExecutionRepository
  │   ├── IStepExecutionRepository
  │   └── IInitExecutionRepository
  └── Admin Services
      ├── IQueryPreviewService
      ├── ICsvTemplateService
      ├── ICsvUploadService
      └── IManualBatchService
```

## Key Flows

### Runbook Publishing

```
POST /api/runbooks  [Admin role required]
    |
    v
Parse YAML → Validate schema → Check name matches
    |
    v
Get max version → Deactivate previous → Insert new record
    |
    v
Return { runbookId, version, dataTableName }
```

Each publish creates a new version. Only one version is active per runbook name. The YAML is stored raw and parsed on demand.

### Manual Batch Creation

```
POST /api/batches  [Admin role required]
    |
    v
Parse multipart form → Get runbook → Parse YAML
    |
    v
CsvUploadService.ParseCsvAsync()
  - Validate primary key column exists
  - Check for duplicate keys
  - Parse all rows into DataTable
    |
    v
ManualBatchService.CreateBatchAsync()
  - Create batch record (is_manual=1, created_by=caller)
  - Create/update dynamic table with CSV data
  - Insert batch_members
  - Create phase_executions (status=pending, due_at=NULL)
  - Create init_executions (if init steps defined)
    |
    v
Return { batchId, status, memberCount, availablePhases }
```

### Manual Batch Advancement

```
POST /api/batches/{id}/advance  [Admin role required]
    |
    v
Validate batch is manual (is_manual=1)
    |
    v
Check current state:
  ├── Init pending?     → Dispatch init steps via Service Bus
  ├── Init in progress? → Return error (wait for completion)
  ├── All phases done?  → Return success (nothing to advance)
  └── Next phase ready? → Verify previous phase complete
                            → Dispatch phase via Service Bus
                            → Update phase status + batch.current_phase
```

The orchestrator picks up dispatched phases from Service Bus and coordinates step execution across cloud workers.

### Query Preview

```
POST /api/runbooks/{name}/query/preview  [Admin role required]
    |
    v
Get runbook → Parse YAML → Extract data source config
    |
    v
DataSourceQueryService.ExecuteQueryAsync()
  - Connect to Databricks or Dataverse
  - Execute the runbook's SQL query
    |
    v
Return { rowCount, columns, sample (up to 100 rows), batchGroups }
```

No side effects — purely read-only. Shows how members would be grouped into batches.

## Data Model

### Core Tables

```
runbooks
  ├── id, name, version, yaml_content
  ├── data_table_name, is_active
  ├── overdue_behavior, rerun_init
  └── created_at

batches
  ├── id, runbook_id, batch_start_time
  ├── status, is_manual, created_by
  ├── current_phase, detected_at
  └── init_dispatched_at

batch_members
  ├── id, batch_id, member_key
  ├── status, added_at, removed_at
  └── add_dispatched_at, remove_dispatched_at

phase_executions
  ├── id, batch_id, phase_name
  ├── offset_minutes, due_at, status
  ├── runbook_version
  └── dispatched_at, completed_at

step_executions
  ├── id, phase_execution_id, batch_member_id
  ├── step_name, step_index, worker_id
  ├── function_name, status
  ├── is_poll_step, poll_count
  └── dispatched_at, completed_at, error_message

init_executions
  ├── id, batch_id, step_name, step_index
  ├── status
  └── dispatched_at, completed_at
```

### Admin-Specific Tables

```
runbook_automation_settings
  ├── runbook_name, automation_enabled
  ├── enabled_at, enabled_by
  └── disabled_at, disabled_by
```

### Dynamic Tables

Created by the shared library when CSV data is uploaded or queries are executed:

```
runbook_{sanitized_name}_v{version}
  ├── {primary_key_column}  (from runbook definition)
  ├── {data_columns...}     (from query or CSV)
  └── batch_time            (if batch_time_column defined)
```

## Infrastructure

### Azure Resources (via Bicep)

| Resource | Purpose |
|----------|---------|
| Flex Consumption Function App | Hosts the API (.NET 8 isolated) |
| Storage Account | Azure Functions runtime storage |
| Application Insights | Telemetry and logging |
| Managed Identity | Authenticates to Key Vault, Service Bus |

### App Settings

| Setting | Description |
|---------|-------------|
| `AdminApi__SqlConnectionString` | SQL Server connection string |
| `AdminApi__ServiceBusNamespace` | Service Bus FQDN (optional) |
| `AzureAd__Instance` | `https://login.microsoftonline.com/` |
| `AzureAd__TenantId` | Entra ID tenant ID |
| `AzureAd__ClientId` | App registration client ID |
| `AzureAd__Audience` | API audience URI |

### Local Development

`local.settings.json` (gitignored) provides local dev configuration:

```json
{
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AdminApi__SqlConnectionString": "Server=localhost;...",
    "AzureAd__Instance": "https://login.microsoftonline.com/",
    "AzureAd__TenantId": "YOUR_TENANT_ID",
    "AzureAd__ClientId": "YOUR_CLIENT_ID",
    "AzureAd__Audience": "api://YOUR_CLIENT_ID"
  }
}
```

## Testing Strategy

Tests are organized by layer:

- **Service layer tests** (58 tests) — Unit tests with mocked repositories. Cover CSV parsing, template generation, query preview, and batch management logic.
- **Auth layer tests** (29 tests) — Verify claim extraction, `[Authorize]` attribute presence on all endpoints, correct policy assignments (Admin vs Authenticated), and `AuthorizationLevel.Anonymous` on all HTTP triggers.

Function-layer integration tests are not included — the functions are thin wrappers that delegate to tested services, and auth is verified via reflection.
