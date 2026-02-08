# Pre-Deployment Code Review: `src/automation/` and `infra/automation/`

## Context

Full codebase review ahead of initial Azure deployment, focused on issues that will be **difficult or impossible to change after deployment** — schema decisions, infrastructure topology, cross-service contracts, and resource configurations that require recreation or data migration to fix. Organized into tiers by deployment-lock-in risk.

---

## Tier 1: SQL Schema Drift — **ALL FIXED**

The SQL schema file was **out of sync** with the C# models and repository code. All issues resolved in `e754c60`.

### 1.1 Missing `runbook_automation_settings` table — **FIXED**
- **C# code**: `AutomationSettingsRepository` queries `runbook_automation_settings` table via MERGE/SELECT
- **schema.sql**: Table does not exist
- **Impact**: Admin API `/api/runbooks/{name}/automation` endpoints will crash on first call
- **Files**: `src/automation/shared/.../Repositories/AutomationSettingsRepository.cs`, `src/automation/database/schema.sql`
- **Resolution**: Table added to `schema.sql` with matching columns and constraints.

### 1.2 Missing columns and wrong constraints in `batches` table — **FIXED**
- **C# model** (`BatchRecord.cs`): Has `IsManual`, `CreatedBy`, `CurrentPhase` properties
- **schema.sql**: `batches` table has none of these columns
- **schema.sql line 26**: `batch_start_time DATETIME2 NOT NULL` — but manual batches set this to NULL (`ManualBatchService.cs:107`)
- **schema.sql line 30**: `UNIQUE (runbook_id, batch_start_time)` — NULL values break this constraint for multiple manual batches of the same runbook
- **Impact**: Manual batch INSERT will fail with NOT NULL violation; schema can't support the manual batch feature at all
- **Fix**: Change to `batch_start_time DATETIME2` (nullable), add columns, adjust UNIQUE constraint to handle NULLs
- **Files**: `src/automation/shared/.../Models/Db/BatchRecord.cs:14-17`, `src/automation/database/schema.sql:23-33`
- **Resolution**: Added `is_manual`, `created_by`, `current_phase` columns; made `batch_start_time` nullable; adjusted UNIQUE constraint for NULL handling.

### 1.3 Missing `on_failure` column in `step_executions` and `init_executions` — **FIXED**
- **C# models**: Both `StepExecutionRecord` and `InitExecutionRecord` have `OnFailure` property
- **schema.sql**: Neither table has this column
- **Impact**: Rollback trigger tracking doesn't persist — orchestrator can't determine which rollback sequence to invoke after restart
- **Files**: `src/automation/shared/.../Models/Db/StepExecutionRecord.cs:27`, `src/automation/database/schema.sql:66-91`
- **Resolution**: Added `on_failure` column to both `step_executions` and `init_executions` tables.

### 1.4 `phase_executions` status CHECK allows `in_progress` but constant doesn't exist — **FIXED**
- **schema.sql line 62**: CHECK constraint includes `'in_progress'`
- **PhaseStatus.cs**: Defines `Pending`, `Dispatched`, `Completed`, `Skipped`, `Failed` — no `InProgress`
- **Impact**: Unused status value in SQL wastes a CHECK slot; confusing if someone sets it directly via SQL

- **Resolution**: Removed `'in_progress'` from `phase_executions` CHECK constraint to match `PhaseStatus.cs` constants.

### Fix (all 1.x) — Completed in `e754c60`
Updated `schema.sql` to match the C# models:
- Added `runbook_automation_settings` table
- Added `is_manual BIT NOT NULL DEFAULT 0`, `created_by NVARCHAR(256)`, `current_phase NVARCHAR(128)` to `batches`
- Added `on_failure NVARCHAR(256)` to both `step_executions` and `init_executions`
- Removed `'in_progress'` from `phase_executions` CHECK constraint

---

## Tier 2: Infrastructure (hard to change after deployment)

### 2.1 SQL Server has no connectivity path — **FIXED**
- **deploy.bicep line 96**: `publicNetworkAccess: 'Disabled'`
- No firewall rules, no private endpoint, no VNet service endpoint
- **Impact**: Deployment succeeds but Function Apps cannot reach SQL — every query times out
- **Fix**: Add `Microsoft.Sql/servers/firewallRules` with `0.0.0.0`-`0.0.0.0` (Allow Azure Services), or configure VNet + private endpoint
- **File**: `infra/automation/scheduler-orchestrator/deploy.bicep:88-98`
- **Resolution**: VNet private networking wired up — `infra/shared/network.bicep` provides SQL private endpoint + DNS zone. Function Apps get VNet integration via subnet IDs. Scheduler/orchestrator Function Apps have `publicNetworkAccess: 'Disabled'` when VNet is active.

### 2.2 SQL Database `autoPauseDelay` on a non-serverless SKU — **FIXED**
- **deploy.bicep line 106-112**: SKU is `S0` (Standard provisioned) but `autoPauseDelay: 60` is set
- `autoPauseDelay` is a **serverless-only** property — it's silently ignored on Standard tier, or could cause deployment errors depending on API version
- **Fix**: Either switch to serverless SKU (`name: 'GP_S_Gen5_1'`, `tier: 'GeneralPurpose'`) and keep `autoPauseDelay`, or remove `autoPauseDelay` for the S0 SKU
- **File**: `infra/automation/scheduler-orchestrator/deploy.bicep:100-114`
- **Resolution**: Switched SQL Database to serverless SKU (`GP_S_Gen5_1` / `GeneralPurpose`) to match `autoPauseDelay` usage.

### 2.3 Service Bus duplicate detection disabled — **FIXED**
- All three topics have `requiresDuplicateDetection: false`
- The orchestrator relies on deterministic `JobId` values for idempotency, but without duplicate detection at the broker level, duplicate messages from retries can cause double-dispatch
- **Hard to change later**: Enabling duplicate detection requires **recreating the topic** (cannot be toggled on existing topics)
- **Fix**: Set `requiresDuplicateDetection: true` and `duplicateDetectionHistoryTimeWindow: 'PT10M'` on all three topics
- **File**: `infra/shared/deploy.bicep:56-90`
- **Resolution**: Enabled `requiresDuplicateDetection: true` with `duplicateDetectionHistoryTimeWindow: 'PT10M'` on all three topics in `deploy.bicep`.

### 2.4 WorkerDispatcher sets `SessionId` but sessions aren't enabled (misleading, not broken) — **FIXED**
- `WorkerDispatcher.cs:44,78` sets `SessionId = job.WorkerId` on messages
- Routing actually works via SQL filter on the `WorkerId` application property (cloud-worker subscription, `deploy.bicep:130-138`)
- Cloud-worker uses `CreateReceiver` (non-session receiver) — this is correct
- The `SessionId` field is stored but never used for routing — misleading but not harmful
- **Fix**: Remove `SessionId` assignment from `WorkerDispatcher.cs` to avoid confusion, OR document that SQL filters (not sessions) handle worker routing
- **File**: `src/automation/orchestrator/.../Services/WorkerDispatcher.cs:44,78`
- **Resolution**: Removed misleading `SessionId` assignment from `WorkerDispatcher.cs`; routing uses SQL filters on the `WorkerId` application property as intended.

### 2.5 Default public network access on Key Vault and Service Bus — **FIXED**
- `disablePublicNetworkAccess` defaults to `false` — secrets and message bus are publicly accessible
- **Fix**: Change default to `true` in production parameter files, or add a note that dev deployments are intentionally public
- **File**: `infra/shared/deploy.bicep:15`, `deploy.parameters.json:9`
- **Resolution**: Split into per-service params: `enableKeyVaultFirewall` (defaults `true`, public endpoint enabled but firewalled with deny-by-default + trusted Azure service bypass for Arc hybrid workers; VNet resources use private endpoint) and `disableServiceBusPublicAccess` (defaults `false`, Standard SKU has no PE support).

### 2.6 ACR Basic SKU — no SLA, no geo-replication — **DEFERRED** (sandbox)
- Cloud-worker image pulls will fail if ACR is unavailable (Basic has no SLA)
- **Fix**: Use `Standard` SKU for any non-dev environment
- **File**: `infra/shared/deploy.bicep:118-128`
- **Note**: Kept Basic for sandbox — no PE support without Premium ($$$), managed identity pulls use Azure backbone. Upgrade to Standard/Premium for production.

### 2.7 Application Insights retention inconsistent — **FIXED**
- Admin-api: explicit `RetentionInDays: 30`
- Scheduler, orchestrator, cloud-worker: no retention set (defaults to 90 days)
- **Fix**: Add `RetentionInDays: 30` to all App Insights resources
- **Files**: `infra/automation/scheduler-orchestrator/deploy.bicep:164-173,268-277`, `infra/automation/cloud-worker/deploy.bicep:88-97`
- **Resolution**: Added explicit `RetentionInDays: 30` to scheduler, orchestrator, and cloud-worker App Insights resources for consistency with admin-api.

---

## Tier 3: Cross-Service Contract Issues (painful to change once workers are running)

### 3.1 WorkerResultStatus casing mismatch — **FIXED**
- `WorkerResultStatus.cs`: `"Success"`, `"Failure"` (PascalCase)
- All other status constants: lowercase (`"pending"`, `"dispatched"`, `"completed"`, `"failed"`)
- `ResultProcessor.cs:92`: `if (result.Status == "Success")` — hardcoded PascalCase match
- Cloud-worker `job-dispatcher.ps1` also uses PascalCase to match
- **Risk**: Any future worker implementation that sends `"success"` (lowercase, matching convention) silently fails result processing
- **Fix**: Standardize on lowercase `"success"` / `"failure"` in `WorkerResultStatus.cs`, update `ResultProcessor.cs`, and update cloud-worker's `job-dispatcher.ps1`
- **Files**: `src/automation/shared/.../Constants/WorkerResultStatus.cs`, `src/automation/orchestrator/.../Handlers/ResultProcessor.cs:92`, `src/automation/cloud-worker/src/job-dispatcher.ps1`
- **Resolution**: Standardized to lowercase `"success"` / `"failure"` in `WorkerResultStatus.cs`, updated `ResultProcessor.cs` comparison, and updated cloud-worker `job-dispatcher.ps1` to match.

### 3.2 Orchestrator `prefetchCount: 0` — poor throughput — **FIXED**
- `host.json:22`: Messages fetched one-at-a-time despite `maxConcurrentCalls: 16`
- **Fix**: Set `prefetchCount` to `16` or `32` (match or exceed `maxConcurrentCalls`)
- **File**: `src/automation/orchestrator/src/Orchestrator.Functions/host.json:22`
- **Resolution**: Set `prefetchCount` to `16` to match `maxConcurrentCalls`.

### 3.3 ~~Orchestrator: missing correlation data → silent message loss~~ FIXED
- `ResultProcessor.cs:59-63`: If `CorrelationData` is null, logs warning and returns — message is completed (not dead-lettered)
- **Fix**: Dead-letter the message instead of completing it, so operators can investigate
- **File**: `src/automation/orchestrator/.../Handlers/ResultProcessor.cs:59-63`
- **Resolution**: Moved null/invalid correlation checks into `WorkerResultFunction` — null correlation dead-letters with `MissingCorrelationData`, invalid correlation (present but missing both execution IDs) dead-letters with `InvalidCorrelationData`. `ProcessAsync` now returns `Task<bool>` to signal invalid correlation.

---

## Tier 4: Application-Level Issues (fix before production load)

### 4.1 Admin API: no global error handling — **FIXED**
- Each function has its own try/catch; unhandled exceptions expose stack traces
- **Fix**: Add exception-handling middleware in `Program.cs` that returns sanitized errors
- **File**: `src/automation/admin-api/src/AdminApi.Functions/Program.cs`
- **Resolution**: Added `ExceptionHandlingMiddleware` (`IFunctionsWorkerMiddleware`) that catches unhandled exceptions, logs at Error level (full exception to App Insights), and returns `{ "error": "An internal error occurred." }` with 500 status. Registered in `Program.cs` via `UseMiddleware`. Existing per-function try/catch blocks retained for expected input validation (400s).

### 4.2 Admin API: CSV upload doesn't validate required columns present — **FIXED**
- `CsvUploadService` checks for unexpected columns but does NOT verify all expected columns exist
- A batch created with missing columns will fail at template resolution time
- **Fix**: Add missing-column validation in `CsvUploadService.ParseCsvAsync()`
- **File**: `src/automation/admin-api/src/AdminApi.Functions/Services/CsvUploadService.cs`
- **Resolution**: Added required-column validation after primary key check — compares CSV headers against `GetExpectedColumns()` (primary key, batch time column, multi-valued columns, and template-referenced columns). Missing columns returned as errors with specific column names. Case-insensitive matching. 4 new tests added.

### 4.3 Admin API: Service Bus is optional — manual batch advancement silently skips publishing — **FIXED**
- `Program.cs`: `ServiceBusClient` is nullable; `ManualBatchService` skips publish if null
- Batch appears "advanced" to the user but orchestrator never receives the event
- **Fix**: Make Service Bus required (throw on startup if not configured)
- **File**: `src/automation/admin-api/src/AdminApi.Functions/Program.cs:48-54`
- **Resolution**: Made `ServiceBusNamespace` required with `[Required]` annotation (caught by `ValidateOnStart`). Made `ServiceBusClient` non-nullable in DI and `ManualBatchService`. Removed null guards — publish calls are now unconditional. Additionally moved step creation from scheduler's `PhaseDispatcher` into orchestrator's `PhaseDueHandler`, so both scheduler and admin-api are thin dispatchers that send `PhaseDueMessage` and the orchestrator owns all step creation, template resolution, and worker dispatch.

### 4.4 Scheduler: unresolved template variables passed silently to workers
- `TemplateResolver.cs`: If a `{{column}}` doesn't match query data, the literal `{{column}}` string is sent to the worker
- **Fix**: Either throw or mark the step as failed instead of dispatching bad parameters
- **File**: `src/automation/shared/.../Services/TemplateResolver.cs`

### 4.5 Scheduler: YAML parse errors silently skip runbook
- `SchedulerTimerFunction.ProcessRunbookAsync()`: Generic catch logs error but continues
- Operator may not notice a runbook is dead
- **Fix**: Log at Critical severity; consider adding a "last_error" field to the runbook table
- **File**: `src/automation/scheduler/src/Scheduler.Functions/Functions/SchedulerTimerFunction.cs`

### 4.6 Cloud-worker: app secret never refreshed
- Secret fetched once at startup, used for the entire worker lifetime
- **Fix**: Add periodic refresh (e.g., every 10 minutes) or catch 401 errors and refresh on demand
- **File**: `src/automation/cloud-worker/src/worker.ps1:84-91`

### 4.7 Cloud-worker: idle timeout can fire mid-job
- `job-dispatcher.ps1`: `lastActivityTime` resets on message receipt, but if a job runs longer than the idle timeout (300s default), the worker exits while the job is still in-flight
- **Fix**: Reset `lastActivityTime` when checking active jobs, not just on message receipt
- **File**: `src/automation/cloud-worker/src/job-dispatcher.ps1:229-244`

### 4.8 Cloud-worker: unused `throttle-handler.ps1`
- `Invoke-WithThrottleRetry` is loaded but never called — actual retry logic is inline in `runspace-manager.ps1`
- **Fix**: Remove `throttle-handler.ps1` or consolidate retry logic into it
- **Files**: `src/automation/cloud-worker/src/throttle-handler.ps1`, `src/automation/cloud-worker/src/runspace-manager.ps1`

### 4.9 Cloud-worker: 30-second shutdown grace period is hard-coded
- If EXO operations take longer, they get killed mid-execution
- **Fix**: Make configurable via `SHUTDOWN_GRACE_SECONDS` environment variable
- **File**: `src/automation/cloud-worker/src/job-dispatcher.ps1:408`

### 4.10 Admin API: member add/remove doesn't dispatch Service Bus messages — **FIXED**
- `MemberManagementFunction` updates the database when members are added (`POST /api/batches/{id}/members`) or removed (`DELETE /api/batches/{batchId}/members/{memberId}`), but never publishes `MemberAddedMessage` or `MemberRemovedMessage` to Service Bus
- **Impact**: Added members don't get catch-up step executions for already-dispatched phases (orchestrator's `MemberAddedHandler` never fires); removed members don't get pending steps cancelled or `on_member_removed` cleanup dispatched (orchestrator's `MemberRemovedHandler` never fires)
- **Fix**: Inject `ServiceBusClient` into `MemberManagementFunction`, publish messages after DB operations (best-effort with try/catch per member), set `add_dispatched_at`/`remove_dispatched_at` timestamps on success
- **Files**: `src/automation/admin-api/src/AdminApi.Functions/Functions/MemberManagementFunction.cs`
- **Resolution**: Added `ServiceBusClient` injection, `PublishMemberAddedAsync`/`PublishMemberRemovedAsync` private methods (same pattern as `ManualBatchService`). `AddAsync` captures member IDs from `InsertAsync`, dispatches per-member after the insert loop. `RemoveAsync` dispatches after `MarkRemovedAsync`. Both are best-effort — publish failure is logged as warning, member operation still succeeds. 7 new tests added.

### 4.11 Runbook version transition handling broken for in-flight batches — **FIXED**

Seven interconnected bugs caused by `batches.runbook_id` being a per-version row ID while queries treated it as version-agnostic. Publishing a new runbook version while batches are in-flight silently orphans them.

**Root cause**: `GetActiveByRunbookAsync(runbook.Id)` and `GetByRunbookAndTimeAsync(runbook.Id, ...)` filter by `runbook_id`, which is the auto-increment row ID for a *specific version*. After publishing v2, `runbook.Id` is the v2 row, but existing batches still reference v1's row ID.

**Sub-issues fixed:**
1. **PhaseDispatcher sends active version instead of phase's version** — `PhaseDispatcher.cs:57` sent `runbook.Version` instead of `phase.RunbookVersion`, causing the orchestrator to load wrong YAML for step creation
2. **MemberAddedHandler uses single version for all catch-up phases** — After version transition, catch-up phases span versions but handler used one definition for all
3. **VersionTransitionHandler never runs** — `GetActiveByRunbookAsync(runbook.Id)` returns zero results for old-version batches, so the handler was never reached
4. **BatchDetector creates duplicate batches** — `GetByRunbookAndTimeAsync(runbook.Id, ...)` can't find v1 batches, creating duplicates under v2
5. **Old-version pending phases not cleaned up** — VersionTransitionHandler created new phases but left old pending phases, causing double execution
6. **Superseded phases block batch completion** — `CheckBatchCompletionAsync` didn't treat superseded phases as terminal
7. **Batch query returns zero results after version publish** — Same root cause as #3/#4

**Fix (`851d832`):**
- Added `GetActiveByRunbookNameAsync` / `GetByRunbookNameAndTimeAsync` — join `batches` to `runbooks` by name
- `PhaseDispatcher` sends `phase.RunbookVersion` in messages
- Added `PhaseStatus.Superseded` constant + schema CHECK constraint update
- `VersionTransitionHandler` calls `SupersedeOldVersionPendingAsync` after creating new phases
- `PhaseDueHandler` and `ResultProcessor` treat `Superseded` as terminal in batch completion checks
- `MemberAddedHandler` loads per-phase version runbook for catch-up step definitions

**Files**: `IBatchRepository.cs`, `BatchRepository.cs`, `SchedulerTimerFunction.cs`, `BatchDetector.cs`, `PhaseDispatcher.cs`, `PhaseStatus.cs`, `schema.sql`, `IPhaseExecutionRepository.cs`, `PhaseExecutionRepository.cs`, `VersionTransitionHandler.cs`, `PhaseDueHandler.cs`, `ResultProcessor.cs`, `MemberAddedHandler.cs`

---

## Tier 5: Deferred / Low-Priority

These are worth tracking but can be addressed post-initial-deployment:

- **DynamicTableManager**: No SQL keyword filtering on column names (regex only blocks special chars)
- **DynamicTableManager**: `json_array` format passthrough doesn't validate JSON
- **PhaseEvaluator**: Seconds→minutes rounding via `Math.Ceiling` is undocumented
- **Orchestrator**: Phase progression race conditions (concurrent handlers can double-dispatch) — mitigated by deterministic JobIds but warrants distributed locking long-term
- **Orchestrator**: `PhaseDueHandler` doesn't update `phase_executions.status` to `dispatched`
- **Admin API**: No pagination on batch list endpoint (hardcoded `TOP 100`)
- **Admin API**: No health check endpoint
- **Admin API**: Inconsistent HTTP status codes across endpoints (400 vs 409 vs 422)
- **Bicep**: No diagnostic settings (SQL audit logs, Key Vault access logs)
- **Bicep**: Storage accounts default to public access when no subnet ID provided
- **Bicep**: KEDA scaler has no explicit `cooldownPeriod` or `pollingInterval`

---

## Implementation Order

1. ~~**Schema fixes** (Tier 1) — cannot deploy without these~~ — **DONE** (`e754c60`)
2. ~~**SQL connectivity + SKU fix** (Tier 2.1, 2.2) — cannot run without these~~ — **DONE** (2.1 via network.bicep, 2.2 via `dd5dfce`)
3. ~~**Service Bus topic config** (Tier 2.3, 2.4) — must be right on first create~~ — **DONE** (2.3 via `d178cb1`, 2.4 via `de8a303`)
4. ~~**Cross-service contracts** (Tier 3) — affects all components~~ — **DONE** (3.1, 3.2, 3.3 all fixed)
5. **Application fixes** (Tier 4) — fix before any real workload
6. ~~**Infrastructure hardening** (Tier 2.5-2.7) — before production~~ — **DONE** (2.5 via network.bicep, 2.7 via `194a8f1`; 2.6 deferred for sandbox)

## Progress Summary

- **Fixed**: 18 issues (1.1, 1.2, 1.3, 1.4, 2.1, 2.2, 2.3, 2.4, 2.5, 2.7, 3.1, 3.2, 3.3, 4.1, 4.2, 4.3, 4.10, 4.11)
- **Deferred**: 1 issue (2.6 — ACR Basic SKU, acceptable for sandbox)
- **Open**: 15 items (4.4–4.9, 11 Tier 5 items) — recommended before production load

## Verification

After implementing:

```bash
# 1. Build all .NET projects
dotnet build src/automation/scheduler/
dotnet build src/automation/admin-api/
dotnet build src/automation/orchestrator/

# 2. Run all test suites
dotnet test src/automation/shared/MaToolkit.Automation.Shared.Tests/
dotnet test src/automation/admin-api/tests/AdminApi.Functions.Tests/
dotnet test src/automation/admin-cli/tests/AdminCli.Tests/

# 3. Run cloud-worker tests
pwsh -File src/automation/cloud-worker/tests/Test-WorkerLocal.ps1

# 4. Manual review: compare schema.sql columns against all *Record.cs models
# 5. Manual review: verify Bicep topic properties include duplicate detection
```
