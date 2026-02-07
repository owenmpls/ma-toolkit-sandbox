# Codebase Review Recommendations — 2026-02-06

Full review of `src/automation/` (cloud-worker, scheduler, admin-api, orchestrator, admin-cli, shared library) and `infra/automation/` (Bicep templates).

---

## Critical Issues

### 1. SQL Injection via String Interpolation (Orchestrator) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — All ~30 interpolated status constants replaced with Dapper `@Parameters`.

**Impact:** All four orchestrator repositories interpolate status constants directly into SQL strings. While the values come from C# constants today, this is fragile and violates secure coding standards.

**Files:**
- `src/automation/orchestrator/src/Orchestrator.Functions/Services/Repositories/BatchRepository.cs` — lines 51-61
- `src/automation/orchestrator/src/Orchestrator.Functions/Services/Repositories/PhaseExecutionRepository.cs` — lines 49, 65, 73
- `src/automation/orchestrator/src/Orchestrator.Functions/Services/Repositories/StepExecutionRepository.cs` — lines 65, 73, 89, 106, 116, 126, 137-138, 148
- `src/automation/orchestrator/src/Orchestrator.Functions/Services/Repositories/InitExecutionRepository.cs` — lines 63, 96, 106, 116, 126, 137-138, 148

**Example — before:**
```csharp
$"UPDATE batches SET status = '{BatchStatus.Completed}' WHERE id = @Id"
```

**Fix:** Parameterize all status values:
```csharp
"UPDATE batches SET status = @Status WHERE id = @Id",
new { Id = id, Status = BatchStatus.Completed }
```

~30 occurrences across the four files.

---

### 2. Unbounded Polling Loop (Shared Library) ✅ FIXED

> **Fixed in:** `2b4f982` (tier 1) — Added 5-minute timeout (150 iterations × 2s) with `TimeoutException` and proper resource disposal.

**Impact:** `DatabricksQueryClient.PollForCompletionAsync` uses `while(true)` with no max iteration limit. A stuck Databricks query polls forever.

**File:** `src/automation/shared/MaToolkit.Automation.Shared/Services/DatabricksQueryClient.cs` — lines 68-97

**Fix:** Add max iterations (60 × 2s = 2 min) and throw `TimeoutException`.

---

### 3. Unvalidated Function Invocation (Cloud Worker) ✅ FIXED

> **Fixed in:** `2b4f982` (tier 1) — Added function name whitelist validation against exported StandardFunctions/CustomFunctions modules before invocation.

**Impact:** `job-dispatcher.ps1` invokes PowerShell functions by name from Service Bus messages without whitelist validation. A compromised message could invoke any function loaded in the runspace.

**File:** `src/automation/cloud-worker/src/job-dispatcher.ps1` — line 198

**Fix:** Validate function names against exported functions from StandardFunctions/CustomFunctions modules before invocation.

---

### 4. Credentials in Plain Text in Infrastructure (Bicep) ✅ FIXED

> **Fixed in:** `2b4f982` (tier 1) — Migrated SQL connection strings to Key Vault references, disabled ACR admin user (uses managed identity AcrPull), added shared VNet module with private endpoints. Also reduced KEDA scaler auth rule from Manage+Listen+Send to Listen only.

**Impact:** SQL credentials, storage account keys, and Service Bus connection strings embedded directly in app settings across all Bicep templates.

**Files:**
- `infra/automation/scheduler/deploy.bicep` — lines 144-146 (SQL credentials inline)
- `infra/automation/admin-api/deploy.bicep` — lines 91-92, 107-109 (storage keys, SQL conn string)
- `infra/automation/orchestrator/deploy.bicep` — lines 135-137 (storage keys)
- `infra/automation/cloud-worker/deploy.bicep` — lines 197-206 (ACR password, SB connection string)

**Fix:** Migrate to Managed Identity for SQL and storage. Use Key Vault references (`@Microsoft.KeyVault(SecretUri=...)`) for anything that must remain a secret.

---

## High Priority Issues

### 5. No Timer Overlap Detection (Scheduler) ✅ FIXED

> **Fixed in:** `2b4f982` (tier 1) — Added blob lease distributed lock (`BlobLeaseDistributedLock`) that acquires a lease before processing and releases on completion.

**Impact:** If a scheduler run exceeds 5 minutes, the next trigger fires concurrently. Concurrent batch detection can create duplicates, member sync can race.

**File:** `src/automation/scheduler/src/Scheduler.Functions/Functions/SchedulerTimerFunction.cs` — lines 52-75

**Fix:** Implement a distributed lock (Azure Redis, SQL advisory lock, or blob lease) before processing.

---

### 6. Race Condition in Step Progression (Orchestrator) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — All status-transition UPDATEs now include `AND status = @ExpectedStatus`. Methods return `bool`; `ResultProcessor` checks return value and skips if another handler already processed the transition.

**Impact:** Between reading step statuses and dispatching jobs, another concurrent handler could modify the same step. No optimistic locking.

**File:** `src/automation/orchestrator/src/Orchestrator.Functions/Services/Handlers/ResultProcessor.cs` — lines 246-325

**Fix:** Use atomic status update (compare-and-swap: `UPDATE ... SET status=@New WHERE id=@Id AND status=@Expected`) before dispatching.

---

### 7. Idempotency Not Enforced for Job Dispatching (Orchestrator) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — All job IDs are now deterministic (`init-{id}`, `step-{id}`, `step-{id}-poll-{count}`, etc.). `WorkerDispatcher` throws `ArgumentException` on empty JobId instead of falling back to `Guid.NewGuid()`.

**Impact:** If a handler crashes after dispatching a job but before updating the database, duplicate jobs can be sent. Service Bus deduplication window may not cover all cases.

**File:** `src/automation/orchestrator/src/Orchestrator.Functions/Services/WorkerDispatcher.cs` — lines 32-55

**Fix:** Use deterministic message IDs that include step execution ID (`$"{step.Id}:{job.JobId}"`), or update DB status before dispatching.

---

### 8. Service Bus Resources Leaked on Startup Failure (Cloud Worker) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — Added `$sbReceiver`, `$sbSender`, `$sbClient` disposal in Phase 6 catch block.

**Impact:** If runspace pool initialization fails, Service Bus client/receiver/sender created earlier are never disposed.

**File:** `src/automation/cloud-worker/src/worker.ps1` — lines 97-118

**Fix:** Wrap in try/finally to ensure disposal of SB resources.

---

### 9. N+1 Query Pattern (Scheduler & Admin API) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — Hoisted `GetActiveByBatchAsync` and `LoadMemberDataAsync` above the phase loop. Added `RunbookRepository.GetByIdAsync()` and replaced `GetActiveRunbooksAsync()` + LINQ in both `AdvanceAsync` and `AddAsync`.

**Impact:** `PhaseDispatcher` calls `GetActiveByBatchAsync()` and `LoadMemberDataAsync()` per phase instead of once per batch. `BatchManagementFunction.AdvanceAsync` fetches ALL active runbooks instead of one by ID.

**Files:**
- `src/automation/scheduler/src/Scheduler.Functions/Services/PhaseDispatcher.cs` — lines 49-145
- `src/automation/admin-api/src/AdminApi.Functions/Functions/BatchManagementFunction.cs` — lines 302-305

**Fix:** Load data once outside loops. Add `GetByIdAsync()` to runbook repository.

---

### 10. No Dead-Letter Queue Handling (Orchestrator) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — Null deserialization now dead-letters the message instead of silently completing. New `WorkerResultDlqFunction` logs dead-lettered messages at Error severity and completes them.

**Impact:** Messages that exceed max retry count go to DLQ with no alerting, monitoring, or recovery. Silently lost.

**File:** `src/automation/orchestrator/src/Orchestrator.Functions/Functions/WorkerResultFunction.cs` — lines 36-57

**Fix:** Add a DLQ handler function that logs high-severity events and triggers alerts.

---

### 11. Missing File Size Validation on CSV Uploads (Admin API) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — Added 50MB file size limit check in both `BatchManagementFunction.CreateAsync` and `MemberManagementFunction.AddAsync`.

**Impact:** No file size limit on CSV uploads. `CsvUploadService` reads the entire file into memory. A large upload causes OOM.

**Files:**
- `src/automation/admin-api/src/AdminApi.Functions/Functions/BatchManagementFunction.cs` — lines 157-160
- `src/automation/admin-api/src/AdminApi.Functions/Functions/MemberManagementFunction.cs` — lines 106-112
- `src/automation/admin-api/src/AdminApi.Functions/Services/CsvUploadService.cs` — lines 38-39

**Fix:** Add file size limit (e.g., 50MB). Consider streaming parser for large files.

---

### 12. Blocking Async in DynamicTableReader (Orchestrator) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — Replaced synchronous `conn.Open()` and `SqlDataAdapter.Fill()` with Dapper `QueryAsync`. Removed explicit `conn.Open()` and `SqlConnection` cast in `GetMembersDataAsync`.

**Impact:** Methods marked `async` use synchronous `conn.Open()` and `SqlDataAdapter.Fill()`, blocking thread pool threads.

**File:** `src/automation/orchestrator/src/Orchestrator.Functions/Services/DynamicTableReader.cs` — lines 37-124

**Fix:** Use `OpenAsync()` and async alternatives.

---

### 13. Bare Exception Catches (Shared Library) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — Replaced bare `catch` with `catch (InvalidOperationException)` in both `IsPollingInProgress()` and `GetPollingResultData()`.

**Impact:** Catches all exceptions including `OutOfMemoryException`, `StackOverflowException`. Makes debugging difficult.

**File:** `src/automation/shared/MaToolkit.Automation.Shared/Models/Messages/WorkerResultMessage.cs` — lines 54-56, 78-79

**Fix:** Catch `JsonException` specifically.

---

### 14. Fire-and-Forget Publishing (Scheduler) ✅ FIXED

> **Fixed in:** `3cf018e` (tier 2) — Wrapped publish calls in try/catch so failures don't crash the loop. Added `RetryUndispatchedAdditionsAsync` and `RetryUndispatchedRemovalsAsync` sweep at start of each sync cycle to re-publish for members missing dispatch timestamps.

**Impact:** In `MemberSynchronizer`, if `PublishMemberAddedAsync` fails after DB insert, the member is marked active but the orchestrator never receives the event. Member is orphaned.

**File:** `src/automation/scheduler/src/Scheduler.Functions/Services/MemberSynchronizer.cs` — lines 68-76

**Fix:** Use the outbox pattern (write event to local table in same transaction, publish separately) or span transaction across insert and dispatch flag update.

---

## Medium Priority Issues

### 15. Overly Permissive Firewall/Access Rules (Infra) ✅ PARTIALLY FIXED

> **Partially fixed in:** `2b4f982` (tier 1) — Disabled SQL public network access and removed permissive firewall rule. Reduced KEDA scaler auth from Manage+Listen+Send to Listen only. Disabled ACR admin user. Added shared VNet module with private endpoints for SQL, Key Vault, Service Bus, and VNet integration for all Function Apps and ACA.

- `infra/automation/scheduler/deploy.bicep:200-207` — SQL firewall `0.0.0.0` allows any Azure service
- `infra/automation/cloud-worker/deploy.bicep:168-178` — KEDA SAS policy has `Manage` rights (only needs `Listen`)
- `infra/automation/cloud-worker/deploy.bicep:145-148` — Container Registry admin user enabled
- No private endpoints or VNet integration on any resource

---

### 16. Repository Duplication Across Projects ✅ FIXED

> **Fixed in:** `0e976d0` — Consolidated all 7 repository types (AutomationSettings, Runbook, Batch, Member, PhaseExecution, StepExecution, InitExecution) into `MaToolkit.Automation.Shared.Services.Repositories`. Deleted 20 duplicate files across admin-api (7), scheduler (7), and orchestrator (6). Each shared interface is the union of all methods from all projects. Also fixed SQL injection in orchestrator's `MemberRepository.GetActiveByBatchAsync` and standardized `UpdateLastPolledAsync` → `UpdatePollStateAsync`.

`BatchRepository`, `MemberRepository`, `PhaseExecutionRepository`, etc. are duplicated across admin-api, scheduler, and orchestrator. Schema changes require updates in 3+ places.

**Fix:** Move shared repository implementations to the shared library. Expose interfaces.

---

### 17. ~~Missing Input Validation Throughout~~ FIXED

- ~~`DataSourceQueryService.cs:23-36` — No null checks on `config` or `config.Type`~~
- ~~`DataverseQueryClient.cs:19-22` — No validation on `query` parameter~~
- ~~`DbConnectionFactory.cs:10-13` — No validation that connection string is non-empty~~
- Admin API request DTOs — not applicable (Azure Functions isolated worker doesn't enforce data annotations; endpoints already validate inline)

---

### 18. Inconsistent Error Handling Strategies (Orchestrator) ✅ FIXED

> **Fixed in:** Removed try-catch that swallowed exceptions in `MemberRemovedHandler` dispatch loop. Exceptions now bubble up to `OrchestratorEventFunction`, consistent with all other handlers. Deterministic job IDs (`removed-{batchMemberId}-{stepIndex}`) ensure Service Bus deduplication prevents duplicate processing on retry.

`OrchestratorEventFunction.cs` throws on error (enabling Service Bus retry), but `MemberRemovedHandler.cs:155-160` catches and swallows exceptions.

**Fix:** Document and enforce a consistent retry strategy across all handlers.

---

### 19. No Correlation IDs

No correlation ID passed through the job-dispatch-result pipeline. Makes cross-service debugging very difficult.

**Fix:** Add correlation ID to all Service Bus messages and include in structured logging.

---

### 20. Docker Container Security (Cloud Worker)

- No `HEALTHCHECK` instruction in Dockerfile (despite health endpoint on port 8080)
- Container runs as root (no `USER` directive)
- No resource limits in `docker-compose.yml`

---

### 21. Missing Tags on Infrastructure Resources

Cloud worker, scheduler, and orchestrator Bicep templates define no `tags` parameter. Admin API has minimal tags. Prevents cost tracking and organizational compliance.

---

### 22. Hardcoded Timeouts and Config Values

- `DataverseQueryClient.cs:32` and `DatabricksQueryClient.cs:43` — 120s timeout hardcoded
- `DatabricksQueryClient.cs:35` — Databricks Entra ID app ID hardcoded as GUID with no comment
- `host.json` — `maxConcurrentCalls: 16` hardcoded
- Service Bus message TTL varies (1 day vs 14 days) with no consistency

---

### 23. No Startup Configuration Validation

No project validates required configuration at startup. Errors only surface at request/processing time.

**Fix:** Validate in `Program.cs` and fail fast.

---

### 24. HTTP Client Missing Timeout (Admin CLI)

`AdminApiClient.cs:27` — `new HttpClient()` with no timeout. Requests can hang indefinitely.

**Fix:** Set `Timeout = TimeSpan.FromSeconds(30)`.

---

### 25. Duplicated Code in Admin CLI

`GetApiUrlOption()` is copy-pasted across 5 command files.

**Fix:** Extract to shared `CommandHelpers` class.

---

### 26. Duplicate Auth Scriptblock (Cloud Worker)

`runspace-manager.ps1` lines 82-108 contains an inline auth scriptblock that duplicates `Get-RunspaceAuthScriptBlock` in `auth.ps1` lines 164-208. The function in `auth.ps1` is defined but never called.

**Fix:** Use `Get-RunspaceAuthScriptBlock` instead of the inline copy.

---

### 27. Magic Strings for Message Types and Status Values

- Message type strings (`"batch-init"`, `"phase-due"`, etc.) repeated in message classes
- `BatchMemberRecord.Status` defaults to `"active"` string instead of `MemberStatus.Active` constant
- No `OverdueBehavior` constants for `"rerun"` / `"ignore"`

**Fix:** Create centralized constants.

---

## Low Priority / Nice-to-Have

| Issue | Location |
|-------|----------|
| Missing XML documentation on shared models | `StepExecutionRecord.cs`, `InitExecutionRecord.cs` |
| No `CancellationToken` support in scheduler/CLI async methods | Throughout |
| `GroupByBatchTimeAsync` is synchronous wrapped in `Task.FromResult` | `BatchDetector.cs:91` |
| No PSScriptAnalyzer config for cloud-worker linting | Project-wide |
| No `ConfigureAwait(false)` in library async code | Throughout .NET projects |
| Missing consistent error response format in admin-api | All function endpoints |
| `auth.ps1` OAuth token request has no timeout | `auth.ps1:101` |
| Inconsistent HTTP status codes in admin-api | `BatchManagementFunction.cs`, `MemberManagementFunction.cs` |
| No health check endpoint in admin-api | Missing feature |
| Missing test coverage for `ManualBatchService.CreateBatchAsync` | `ManualBatchServiceTests.cs` |
| No tests for DynamicTableManager, query clients | Shared library |
| Missing Service Bus duplicate detection in Bicep | `cloud-worker/deploy.bicep:71-87` |
| SQL database missing audit/threat detection config | `scheduler/deploy.bicep:188-222` |
| Storage account naming pattern inconsistent | `admin-api/deploy.bicep:26` vs `scheduler/deploy.bicep:34` |
| No orchestrator parameter file in infra | `infra/automation/orchestrator/` |
| Orchestrator creates Service Bus topics that cloud-worker also creates | Deployment conflict |

---

## Cross-Cutting Recommendations

1. **Centralize data access** — Move repository implementations to shared library to eliminate duplication across 3 projects.
2. **Adopt the outbox pattern** — For scheduler-to-orchestrator messaging, write events to local outbox table within same transaction, then publish separately.
3. **Add observability** — Structured logging with correlation IDs, performance metrics, Application Insights custom metrics.
4. **Harden infrastructure** — Managed Identity everywhere, Private Endpoints, diagnostic settings, resource tags.
5. **Validate early, fail fast** — Startup configuration validation in all `Program.cs` files. Input validation on all public API surfaces.
