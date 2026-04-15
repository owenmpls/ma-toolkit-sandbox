# Device Migration Signal API — Design & Implementation Plan

## Context

100K+ managed devices run a PowerShell script every 15 minutes (via Scheduled Task) that checks whether the local user has been migrated to a new M365 tenant. When the API returns `migrated: true`, the script:

1. Resets M365 apps to first-run state (so they reconnect to the new tenant)
2. Prompts the user to reboot
3. Sets up a one-time login script for next boot
4. Cleans up the scheduled task (stopping future check-ins)

The device script then posts execution results back to the API for tracking.

### Requirements

- High-scale check-in endpoint (~111 req/sec sustained, thundering herd of 100K in 30-60 seconds)
- Track device state: last check-in, signal issuance, execution results — per user+device combination
- Short-term: manual way to flag users as migrated (individual + CSV bulk)
- Long-term: automation subsystem signals migration via runbook step completion
- Power BI reporting with <=30 minute latency
- Scalable, simple, secure

## Architecture Overview

```
                         ┌─────────────────────────────────┐
                         │        Device Script            │
                         │  (100K+ devices, every 15 min)  │
                         └──────────┬──────────────────────┘
                                    │ POST /api/device/checkin
                                    │ POST /api/device/result
                                    ▼
                         ┌──────────────────────┐
                         │   Device API          │
                         │   (Flex Consumption)  │
                         │   API key auth        │
                         └──┬───────────┬────────┘
                  sync read │           │ async enqueue
                            ▼           ▼
                 ┌────────────────┐  ┌──────────────┐
                 │ Table Storage  │  │ Storage Queue │
                 │ MigrationStatus│  │ checkin-log   │
                 │ (per-user flag)│  │ result-log    │
                 └────────────────┘  └──────┬───────┘
                                            │ queue trigger
                         ┌──────────────────┘
                         ▼
              ┌────────────────────┐
              │  Queue Processors  │
              │  (same Function App│
              │   async functions) │
              └─────────┬──────────┘
                        │ upsert
                        ▼
              ┌──────────────────┐        ┌─────────┐
              │   Azure SQL      │◄───────│ Power BI│
              │  device_status   │ Direct │         │
              │  migration_status│ Query  └─────────┘
              └──────────────────┘

Admin:
  ┌─────────────┐   Entra ID JWT   ┌──────────────┐
  │ matoolkit CLI├─────────────────►│ Device API   │
  │ / browser    │                  │ /api/admin/* │
  └─────────────┘                  └──────┬───────┘
                                          │ dual-write
                              ┌───────────┴──────────┐
                              ▼                      ▼
                    Table Storage             Azure SQL
                    (MigrationStatus)    (migration_status)
```

## Key Architecture Decisions

| Decision | Choice | Reasoning |
|----------|--------|-----------|
| **Hosting** | Separate Flex Consumption Function App | Different auth model (API key vs JWT), different scale profile from admin-api, independent deployment and scaling |
| **Migration status store** | Table Storage (`MigrationStatus`) | Hot read path: 100K+ point reads per cycle. No connection pooling, no auto-pause cold start, <10ms reads, handles burst without sizing concerns |
| **Device state + reporting store** | Azure SQL (`device_status`, `migration_status`) | Power BI DirectQuery for <1 min latency dashboards. Full SQL for JOINs, aggregations, complex queries. Written async via queue — never on the hot path |
| **Burst handling** | Storage Queue + queue-triggered functions | Decouples device response latency from SQL write throughput. Queue absorbs 100K messages in seconds; processors drain at sustainable pace |
| **Data model** | One row per user+device in `device_status`, upserted on each check-in | ~100K rows, no growth. No append-only log tables, no retention cleanup. App Insights provides audit trail automatically |
| **Device auth** | API key via `X-API-Key` header | Simplest for 100K device scripts distributed via GPO/Intune. Multiple active keys for zero-downtime rotation |
| **Admin auth** | Entra ID JWT bearer (same pattern as admin-api) | Reuses existing auth infrastructure. Admin role required for writes |
| **Logging considered** | Event Hubs rejected; Storage Queue chosen | Event Hub solves problems we don't have (fan-out, ordering, replay, streaming). Storage Queue is simpler, cheaper (~$0.04/month vs ~$11+/month), requires no new infrastructure (built into the Function App's storage account), and handles 20K msg/sec |
| **Scale-up path** | Dedicated provisioned Azure SQL database | Already in the environment, team knows Dapper, richer querying than Table Storage. Not Cosmos DB — overkill and expensive for this access pattern |

## Why Table Storage for the Read Path, SQL for Reporting

The existing Azure SQL database is **serverless with auto-pause at 60 minutes** and is **shared with the scheduler and orchestrator**. Using it for the check-in hot path creates three problems:

1. **Auto-pause cold start** (5-10 seconds) would timeout device scripts if the DB is asleep
2. **Shared capacity** — bursty device load (3,300 req/sec during thundering herd) competes with scheduler/orchestrator on a 0.5 vCore instance
3. **Connection pooling** — Flex Consumption instances spin up/down rapidly during burst, each creating a new connection pool. The serverless DB (~30 max workers) can't serve 50 pools simultaneously

Table Storage avoids all three: no cold start, isolated from other workloads, HTTP-based (no connection pool). SQL is used only for async writes (via queue) and reporting (Power BI DirectQuery) — workloads it handles well.

If the migration status lookup eventually needs richer querying or the scale grows beyond Table Storage limits, the upgrade path is a **dedicated provisioned SQL database** (not Cosmos DB, which is overkill and expensive for a simple key-value lookup pattern).

## Thundering Herd Design

**The problem**: Customers deploy the script to all devices with the same schedule (e.g., `:00`, `:15`, `:30`, `:45`). Natural jitter from script startup and network latency spreads them over 30-60 seconds, but that's still 1,700-3,300 req/sec. Each device checks in for a **different user** (1:1 device-to-user), so per-UPN caching provides zero benefit.

**The solution**: The check-in endpoint does exactly **1 Table Storage point read + 1 queue write** — both sub-10ms operations that scale massively. All SQL writes happen asynchronously via queue-triggered functions.

```
Check-in (synchronous — <20ms):
  Request → Validate API key → Read MigrationStatus from Table Storage
          → Enqueue to Storage Queue → Return { migrated: bool }

Queue processor (asynchronous — drains at controlled pace):
  Storage Queue → Upsert device_status in SQL
```

**Throughput at burst (3,300 req/sec):**

| Operation | Rate | Limit | Headroom |
|-----------|------|-------|----------|
| MigrationStatus point reads | 3,300/sec | 20,000/sec (account), 2,000/sec (per partition) | 6x account, OK per partition with a few domains |
| Queue writes | 3,300/sec | 20,000/sec | 6x |
| SQL upserts (async) | Controlled by queue batch size | ~500/sec on 0.5 vCore serverless | Queue buffers; 100K messages drain in ~3 min |

**Additional hardening:**
- **Client-side jitter**: Script should `Start-Sleep -Seconds (Get-Random -Maximum 900)` on first run. Won't depend on this, but dramatically reduces burst when implemented.
- **Always-ready instances**: 2-3 pre-warmed Flex Consumption instances (~$13/month each) to absorb the initial wave while scale-out kicks in.

## API Endpoints

### Device Endpoints (API key auth via `X-API-Key` header)

```
POST /api/device/checkin
  Body:     { "upn": "user@contoso.com", "deviceName": "DESKTOP-ABC123" }
  Response: { "migrated": true/false }
  Sync:     reads MigrationStatus (Table Storage)
  Async:    enqueues to checkin-log queue → processor upserts device_status (SQL)

POST /api/device/result
  Body:     { "upn": "user@contoso.com", "deviceName": "DESKTOP-ABC123",
              "success": true, "message": "M365 apps reset, reboot prompted" }
  Response: 204 No Content
  Async:    enqueues to result-log queue → processor updates device_status (SQL)
```

### Admin Endpoints (Entra ID JWT auth, Admin role required for writes)

```
PUT    /api/admin/migration/{upn}          — Flag user as migrated (dual-write: Table Storage + SQL)
DELETE /api/admin/migration/{upn}          — Unflag user
GET    /api/admin/migration/{upn}          — Check user's migration status
POST   /api/admin/migration/bulk           — CSV upload (UPN column) to bulk-flag users
GET    /api/admin/devices                  — List device statuses (filterable by domain, signal status)
GET    /api/admin/devices/{deviceName}     — Get specific device's latest state
GET    /api/admin/devices/summary          — Counts: total checked-in, signaled, results received
```

### Utility

```
GET /api/health                            — Health check (anonymous)
```

## Data Model

### Table Storage: `MigrationStatus` — fast lookup for the check-in hot path

| Field | Value | Notes |
|-------|-------|-------|
| PartitionKey | domain (`contoso.com`) | Distributes reads across partitions by email domain |
| RowKey | full UPN (`user@contoso.com`) | Case-normalized on write |
| IsMigrated | bool | |
| MigratedAt | DateTimeOffset | |
| Source | string | `"manual"`, `"csv"`, `"automation"` |
| FlaggedBy | string | Admin UPN or `"orchestrator"` |

~100K rows. Read on every check-in. Written by admin endpoints and (long-term) orchestrator.

### SQL: `migration_status` — mirror of Table Storage for Power BI

```sql
CREATE TABLE migration_status (
    upn             NVARCHAR(256) NOT NULL PRIMARY KEY,
    domain          NVARCHAR(256) NOT NULL,
    is_migrated     BIT NOT NULL DEFAULT 0,
    migrated_at     DATETIME2,
    source          NVARCHAR(64),       -- 'manual', 'csv', 'automation'
    flagged_by      NVARCHAR(256)
);

CREATE INDEX IX_migration_status_domain ON migration_status (domain, is_migrated);
```

### SQL: `device_status` — one row per user+device, upserted on each check-in

```sql
CREATE TABLE device_status (
    upn                 NVARCHAR(256) NOT NULL,
    device_name         NVARCHAR(256) NOT NULL,
    domain              NVARCHAR(256) NOT NULL,
    last_check_in       DATETIME2 NOT NULL,
    check_in_count      INT NOT NULL DEFAULT 1,
    signal_issued       BIT NOT NULL DEFAULT 0,
    signal_issued_at    DATETIME2,
    result_success      BIT,
    result_message      NVARCHAR(MAX),
    result_at           DATETIME2,
    CONSTRAINT PK_device_status PRIMARY KEY (upn, device_name)
);

CREATE INDEX IX_device_status_domain ON device_status (domain);
CREATE INDEX IX_device_status_signal ON device_status (signal_issued, domain);
```

~100K rows, no growth. Each check-in upserts the same row. No append-only log tables, no retention cleanup needed.

### Storage Queues

| Queue | Producer | Consumer | Message |
|-------|----------|----------|---------|
| `checkin-log` | DeviceCheckInFunction | CheckInLogProcessor | `{ upn, deviceName, signalIssued, checkedAt }` |
| `result-log` | ExecutionResultFunction | ResultLogProcessor | `{ upn, deviceName, success, message, submittedAt }` |

Storage Queue can ingest 20,000 messages/sec. Queue-triggered functions process in configurable batches at a sustainable rate.

## Authentication Design

### Device endpoints — API key middleware

```
Request → ApiKeyAuthMiddleware
  ├─ Path starts with /api/device/ → extract X-API-Key header → validate via ApiKeyService
  │     ├─ Valid → continue pipeline
  │     └─ Invalid/missing → 401
  ├─ Path starts with /api/admin/ → skip (JWT middleware handles)
  └─ Path is /api/health → skip (anonymous)
```

`ApiKeyService` loads keys from Key Vault secret `device-api-keys` on startup, caches in memory, refreshes every 5 minutes. Secret format: JSON array of `{ key, name, createdAt, expiresAt }`. Supports multiple active keys for zero-downtime rotation.

Uses `AuthorizationLevel.Anonymous` on all functions — auth handled entirely in middleware, not via Azure Functions host keys (same pattern as admin-api).

### Admin endpoints — Entra ID JWT

Same `Microsoft.Identity.Web` + `[Authorize]` attribute pattern as admin-api. Admin role required for write operations.

## Project Structure

```
src/automation/device-api/
  DeviceApi.sln
  CLAUDE.md
  src/
    DeviceApi.Functions/
      DeviceApi.Functions.csproj          # Azure.Data.Tables, Azure.Identity, Microsoft.Identity.Web,
                                          # Microsoft.Data.SqlClient, Dapper
      Program.cs                          # DI: TableServiceClient, QueueServiceClient, SQL connection
                                          # factory, dual auth middleware, services
      host.json                           # Queue batch size, App Insights sampling
      local.settings.json
      Auth/
        ApiKeyAuthMiddleware.cs           # Route-aware: /api/device/* requires X-API-Key
        ApiKeyService.cs                  # KV-backed key validation with in-memory cache (5min TTL)
        AuthConstants.cs                  # Policy names, role constants
      Settings/
        DeviceApiSettings.cs              # StorageAccountName, SqlConnectionString, ApiKeySecretName
      Models/
        CheckInRequest.cs                 # UPN + DeviceName (with validation attributes)
        CheckInResponse.cs                # { migrated: bool }
        ExecutionResultRequest.cs         # UPN + DeviceName + Success + Message
        BulkFlagRequest.cs                # CSV content
        Entities/
          MigrationStatusEntity.cs        # ITableEntity for Table Storage
        Messages/
          CheckInLogMessage.cs            # Queue message model
          ResultLogMessage.cs             # Queue message model
      Functions/
        # Device endpoints (API key auth, synchronous response)
        DeviceCheckInFunction.cs          # POST /api/device/checkin — read + enqueue + return
        ExecutionResultFunction.cs        # POST /api/device/result — enqueue + return 204
        # Queue processors (async SQL writes)
        CheckInLogProcessor.cs            # Queue trigger → upsert device_status in SQL
        ResultLogProcessor.cs             # Queue trigger → update device_status in SQL
        # Admin endpoints (Entra ID JWT auth)
        MigrationAdminFunction.cs         # CRUD on migration status (dual-write) + bulk CSV
        DeviceStatusFunction.cs           # Device queries + summary (reads from SQL)
        # Utility
        HealthCheckFunction.cs            # GET /api/health
      Services/
        IMigrationStatusService.cs        # Read from Table Storage, dual-write to Table Storage + SQL
        MigrationStatusService.cs
        IDeviceStatusRepository.cs        # SQL CRUD for device_status table (Dapper)
        DeviceStatusRepository.cs
        ICsvMigrationService.cs           # Parse CSV for bulk migration flagging
        CsvMigrationService.cs
        IApiKeyService.cs
        ApiKeyService.cs
      Middleware/
        ExceptionHandlingMiddleware.cs    # Same pattern as admin-api
  tests/
    DeviceApi.Functions.Tests/
      DeviceApi.Functions.Tests.csproj
      Auth/
        ApiKeyServiceTests.cs
        ApiKeyAuthMiddlewareTests.cs
      Services/
        MigrationStatusServiceTests.cs
        DeviceStatusRepositoryTests.cs
        CsvMigrationServiceTests.cs
      Functions/
        DeviceCheckInFunctionTests.cs
        ExecutionResultFunctionTests.cs
        MigrationAdminFunctionTests.cs
        CheckInLogProcessorTests.cs
        ResultLogProcessorTests.cs

infra/automation/device-api/
  deploy.bicep                            # Flex Consumption func app, storage, App Insights, RBAC, PEs
  deploy.parameters.json
```

## Infrastructure

### `infra/automation/device-api/deploy.bicep`

Follows the admin-api Bicep template (`infra/automation/admin-api/deploy.bicep`) with these differences:

- **SQL connection via Key Vault reference** (same pattern as admin-api — `@Microsoft.KeyVault(SecretUri=...)`)
- **No Service Bus dependency** (initially — added when automation integration is built)
- **API key secret** in Key Vault: `device-api-keys`
- **Always-ready instances**: 2-3 pre-warmed instances for burst absorption
- **RBAC grants**:
  - Device-api managed identity → `Storage Table Data Contributor` + `Storage Queue Data Contributor` + `Storage Blob Data Owner` on its storage account
  - Device-api managed identity → `Key Vault Secrets User` on shared Key Vault
  - (Long-term) Orchestrator managed identity → `Storage Table Data Contributor` on device-api storage account
- **SQL**: `db_datareader` + `db_datawriter` roles for the device-api managed identity (created via deployment script, same as scheduler/orchestrator pattern)
- **VNet integration** to `snet-device-api` subnet
- **Storage private endpoints** (blob, queue, table) in `snet-private-endpoints`
- **Public inbound access enabled** — devices call from the internet; no inbound private endpoint (avoids 20-instance scaling cap on Flex Consumption)

### Shared infrastructure change (`infra/shared/deploy.bicep`)

Add subnet to VNet definition (lines 185-334):

```bicep
{
  name: 'snet-device-api'
  properties: {
    addressPrefix: '10.0.5.0/24'
    serviceEndpoints: [ { service: 'Microsoft.Storage' } ]
    delegations: [
      {
        name: 'delegation-web'
        properties: { serviceName: 'Microsoft.App/environments' }
      }
    ]
  }
}
```

Add output: `deviceApiSubnetId`

### SQL schema deployment

Add `device_status` and `migration_status` tables to the automation database via the existing deployment script pattern in `infra/automation/scheduler-orchestrator/deploy.bicep`, or via a new migration script at `src/automation/database/migrations/`.

## Power BI Reporting

Power BI connects to the SQL database via **DirectQuery** for <1 minute latency. Key queries:

```sql
-- Migration progress by domain
SELECT m.domain, COUNT(*) AS total_users,
       SUM(CASE WHEN m.is_migrated = 1 THEN 1 ELSE 0 END) AS migrated
FROM migration_status m GROUP BY m.domain;

-- Devices signaled but no result received
SELECT * FROM device_status
WHERE signal_issued = 1 AND result_success IS NULL;

-- Devices with failed execution
SELECT * FROM device_status
WHERE result_success = 0;

-- Check-in activity (are devices reporting in?)
SELECT domain, COUNT(*) AS active_devices,
       MIN(last_check_in) AS oldest, MAX(last_check_in) AS newest
FROM device_status
WHERE last_check_in > DATEADD(HOUR, -1, GETUTCDATE())
GROUP BY domain;
```

SQL auto-pause is not a concern for reporting because the queue processors write to SQL continuously (devices check in 24/7 during migration), keeping the database awake.

## Long-term Automation Integration

When the automation subsystem completes user migration (a runbook step succeeds):

1. Add `signals_migration: true` property to the runbook YAML step definition
2. In the orchestrator's `ResultProcessor`, when a step with this flag succeeds, write to device-api's MigrationStatus Table Storage via `Azure.Data.Tables` SDK + upsert `migration_status` in SQL
3. RBAC: orchestrator's managed identity gets `Storage Table Data Contributor` on device-api's storage account

Small future change to `src/automation/orchestrator/src/Orchestrator.Functions/Services/Handlers/ResultProcessor.cs` and the shared YAML model. No new Service Bus topics or triggers needed — Table Storage is the integration point.

## Implementation Order

1. **Shared infra** — Add `snet-device-api` subnet to `infra/shared/deploy.bicep`
2. **SQL schema** — Add `device_status` and `migration_status` tables (migration script)
3. **Project scaffold** — Solution, csproj, Program.cs, settings, host.json, local.settings.json
4. **Models + entities** — Request/response models, Table Storage entity, queue message models
5. **Core services** — MigrationStatusService (dual-write), DeviceStatusRepository (Dapper), ApiKeyService, CsvMigrationService
6. **API key middleware** — Route-aware auth middleware
7. **Device endpoints** — CheckIn and Result functions (read + enqueue pattern)
8. **Queue processors** — CheckInLogProcessor and ResultLogProcessor (SQL upserts)
9. **Admin endpoints** — Migration CRUD, bulk CSV, device status queries
10. **Exception handling middleware** — Error normalization
11. **Tests** — Unit tests for services, middleware, functions, queue processors
12. **Bicep template** — `infra/automation/device-api/deploy.bicep` + parameters
13. **CI/CD** — Add to `deploy-infra.yml` and `deploy-apps.yml` workflows
14. **CLAUDE.md** — Project documentation

## Key Files to Reference During Implementation

| Reference | Path | What to reuse |
|-----------|------|---------------|
| Admin-api Program.cs | `src/automation/admin-api/src/AdminApi.Functions/Program.cs` | DI pattern, Entra ID auth setup, App Insights |
| Admin-api middleware | `src/automation/admin-api/src/AdminApi.Functions/Middleware/ExceptionHandlingMiddleware.cs` | Error handling pattern |
| Admin-api auth | `src/automation/admin-api/src/AdminApi.Functions/Auth/` | AuthConstants, UserContextExtensions |
| Admin-api Bicep | `infra/automation/admin-api/deploy.bicep` | Flex Consumption, RBAC, private endpoints |
| SQL connection factory | `src/automation/admin-api/src/AdminApi.Functions/Services/DbConnectionFactory.cs` | Managed identity SQL connection pattern |
| Shared VNet | `infra/shared/deploy.bicep` (lines 185-334) | Subnet definition pattern |
| Database schema | `src/automation/database/schema.sql` | Table naming conventions |
| CSV upload | `src/automation/admin-api/src/AdminApi.Functions/Services/CsvUploadService.cs` | CSV parsing for bulk migration flagging |

## Verification

- `dotnet build` and `dotnet test` pass
- `func start` locally with Azurite (Table Storage + Queue emulation) and local SQL
- End-to-end: flag user → check-in → verify `migrated: true` → post result → verify `device_status` row in SQL
- Burst test: simulate 10K concurrent check-ins → verify response latency <50ms, queue drains within minutes
- API key rotation: add second key → verify both work → remove first
- Admin JWT auth: unauthenticated → 401, non-admin → 403 on writes
- Power BI: connect via DirectQuery → verify dashboard queries return expected data
