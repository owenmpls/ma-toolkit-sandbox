# Orchestrator Contract

This document specifies everything needed to build the orchestrator component. The orchestrator consumes events from the scheduler via Service Bus and coordinates step execution with the ps-cloud-worker.

## Service Bus Configuration

- **Topic**: `orchestrator-events`
- **Subscription**: `orchestrator`
- **Message format**: JSON body with `Content-Type: application/json`
- **Routing property**: Each message has an `ApplicationProperties["MessageType"]` string that identifies the event type. Use this for subscription filter rules if needed.

The subscription is configured with:
- Max delivery count: 10
- Lock duration: 1 minute
- Default message TTL: 14 days
- Dead-lettering on expiration: enabled

## Message Schemas

### 1. batch-init

Sent when a new batch is detected and the runbook has `init` steps. The orchestrator should execute the init steps in order before allowing phase processing for this batch.

```json
{
  "messageType": "batch-init",
  "runbookName": "contoso-mailbox-migration",
  "runbookVersion": 3,
  "batchId": 42,
  "batchStartTime": "2025-03-15T00:00:00Z",
  "memberCount": 150
}
```

**ApplicationProperties**: `MessageType` = `"batch-init"`

**Expected orchestrator behavior**:

1. Read `init_executions` for the given `batchId` from SQL, ordered by `step_index`.
2. For each init execution in order:
   a. Dispatch the job to the worker via Service Bus (the worker's `jobs` topic).
   b. Update `init_executions.status` to `dispatched`, set `dispatched_at`, store `job_id`.
   c. Wait for the worker result on the `results` topic.
   d. On success: update status to `succeeded`, store `result_json`, set `completed_at`.
   e. On polling response: update status to `polling`, set `poll_started_at` and `last_polled_at`. The scheduler will emit `poll-check` messages on the configured interval.
   f. On failure: update status to `failed`, store `error_message`.
3. When all init steps succeed, update `batches.status` to `active`.
4. If any init step fails, update `batches.status` to `failed`.

### 2. phase-due

Sent when a phase's `due_at` time has passed. The scheduler has already pre-created `step_executions` with resolved parameters for every member in the batch.

```json
{
  "messageType": "phase-due",
  "runbookName": "contoso-mailbox-migration",
  "runbookVersion": 3,
  "batchId": 42,
  "phaseExecutionId": 108,
  "phaseName": "pre-notification",
  "offsetMinutes": 7200,
  "dueAt": "2025-03-10T00:00:00Z",
  "memberIds": [501, 502, 503, 504]
}
```

**ApplicationProperties**: `MessageType` = `"phase-due"`

**Expected orchestrator behavior**:

1. Read `step_executions` for the given `phaseExecutionId` from SQL.
2. Group by `step_index` and process step indices in order (step 0 for all members, then step 1, etc.).
3. For each step execution:
   a. Dispatch the job to the worker: `worker_id`, `function_name`, `params_json`.
   b. Update `step_executions.status` to `dispatched`, set `dispatched_at`, store `job_id`.
   c. Collect worker result.
   d. On success: update status to `succeeded`, store `result_json`, set `completed_at`.
   e. On polling response: update status to `polling`, set `poll_started_at` and `last_polled_at`.
   f. On failure: update status to `failed`, store `error_message`. If the step has `on_failure`, trigger rollback (see Rollback Protocol below).
4. When all step executions for the phase succeed, update `phase_executions.status` to `completed`, set `completed_at`.
5. If any step fails (and rollback completes or no rollback is defined), update `phase_executions.status` to `failed`.
6. When all phases for a batch are completed, update `batches.status` to `completed`.

### 3. member-added

Sent when the scheduler detects a new member in an existing active batch.

```json
{
  "messageType": "member-added",
  "runbookName": "contoso-mailbox-migration",
  "runbookVersion": 3,
  "batchId": 42,
  "batchMemberId": 505,
  "memberKey": "newuser@contoso.com"
}
```

**ApplicationProperties**: `MessageType` = `"member-added"`

**Expected orchestrator behavior** (member catch-up protocol):

1. Load the runbook YAML from `runbooks` where `name = runbookName` and `version = runbookVersion`.
2. Parse the YAML to get the phase definitions.
3. Load all `phase_executions` for the batch.
4. For each phase that is already dispatched or completed (i.e., `due_at` has passed):
   a. Load the member's data row from the dynamic data table.
   b. For each step in the phase, resolve templates against the member data.
   c. Insert new `step_executions` records for this member.
   d. Dispatch the jobs to the worker.
5. For future phases (not yet due), the scheduler will include this member when those phases are dispatched normally.

### 4. member-removed

Sent when a member is no longer returned by the data source query.

```json
{
  "messageType": "member-removed",
  "runbookName": "contoso-mailbox-migration",
  "runbookVersion": 3,
  "batchId": 42,
  "batchMemberId": 503,
  "memberKey": "removeduser@contoso.com"
}
```

**ApplicationProperties**: `MessageType` = `"member-removed"`

**Expected orchestrator behavior**:

1. Cancel any pending or dispatched step executions for this member (update status to `cancelled`).
2. Load the runbook YAML and check for `on_member_removed` steps.
3. If `on_member_removed` steps exist:
   a. Load the member's data from the dynamic table.
   b. Resolve templates in the step params.
   c. Dispatch removal steps to the worker.
4. The `batch_members` record has already been marked as `removed` by the scheduler.

### 5. poll-check

Sent by the scheduler when a polling step's interval has elapsed.

```json
{
  "messageType": "poll-check",
  "runbookName": "contoso-mailbox-migration",
  "runbookVersion": 3,
  "batchId": 42,
  "stepExecutionId": 1042,
  "stepName": "start-mailbox-move",
  "pollCount": 5
}
```

**ApplicationProperties**: `MessageType` = `"poll-check"`

**Expected orchestrator behavior** (polling protocol):

1. Read the step execution record (either from `step_executions` or `init_executions`).
2. Check if the poll timeout has been exceeded: `poll_started_at + poll_timeout_sec < now`.
   - If timed out: update status to `poll_timeout`. Handle as a failure (trigger rollback if `on_failure` is set).
3. If not timed out:
   a. Re-dispatch the step to the worker (same `function_name`, `params_json`).
   b. Collect the result.
   c. If the worker returns success: update status to `succeeded`, store `result_json`, set `completed_at`.
   d. If the worker returns failure: update status to `failed`, store `error_message`.
   e. If the worker returns "still polling": leave status as `polling`. The scheduler will send another `poll-check` after the next interval.

## SQL Tables the Orchestrator Reads and Writes

### runbooks (read only)

```
id                    INT           -- PK
name                  NVARCHAR      -- Runbook name
version               INT           -- Version number
yaml_content          NVARCHAR(MAX) -- Full YAML content (parse for rollbacks, on_member_removed, init steps)
data_table_name       NVARCHAR      -- Name of the dynamic data table for this runbook version
is_active             BIT           -- Whether this version is active
overdue_behavior      NVARCHAR      -- "rerun" or "ignore"
ignore_overdue_applied BIT          -- Whether ignore has been applied for version transition
rerun_init            BIT           -- Whether to re-run init steps on version transition
created_at            DATETIME2     -- Creation timestamp
```

The orchestrator reads `yaml_content` to access `rollbacks`, `on_member_removed`, and `init` step definitions. It reads `data_table_name` to know which dynamic table to query for member data.

### batches (read/write)

```
id                    INT           -- PK
runbook_id            INT           -- FK to runbooks
batch_start_time      DATETIME2     -- The batch's reference time
status                NVARCHAR      -- "detected", "init_dispatched", "active", "completed", "failed"
detected_at           DATETIME2     -- When the batch was first detected
init_dispatched_at    DATETIME2     -- When batch-init was dispatched
```

The orchestrator updates `status` to `active` (after init completes), `completed`, or `failed`.

### batch_members (read only for data, write for cancellation)

```
id                    INT           -- PK
batch_id              INT           -- FK to batches
member_key            NVARCHAR      -- The primary key value identifying this member
status                NVARCHAR      -- "active", "removed"
added_at              DATETIME2     -- When the member was added
removed_at            DATETIME2     -- When the member was removed (null if still active)
add_dispatched_at     DATETIME2     -- When member-added was dispatched
remove_dispatched_at  DATETIME2     -- When member-removed was dispatched
```

### phase_executions (read/write)

```
id                    INT           -- PK
batch_id              INT           -- FK to batches
phase_name            NVARCHAR      -- Name of the phase
offset_minutes        INT           -- Offset from batch_start_time in minutes
due_at                DATETIME2     -- Absolute time when this phase becomes due
runbook_version       INT           -- Which runbook version created this execution
status                NVARCHAR      -- "pending", "dispatched", "completed", "failed", "skipped"
dispatched_at         DATETIME2     -- When the scheduler dispatched this phase
completed_at          DATETIME2     -- When all steps completed (set by orchestrator)
```

The orchestrator updates `status` to `completed` or `failed`, and sets `completed_at`.

### step_executions (read/write -- primary orchestrator table)

```
id                    INT           -- PK
phase_execution_id    INT           -- FK to phase_executions
batch_member_id       INT           -- FK to batch_members
step_name             NVARCHAR      -- Step name from the runbook
step_index            INT           -- Order within the phase (0-based)
worker_id             NVARCHAR      -- Which worker pool to target
function_name         NVARCHAR      -- Resolved function name
params_json           NVARCHAR(MAX) -- Resolved parameters as JSON
status                NVARCHAR      -- See state machine below
job_id                NVARCHAR      -- Worker job ID (set by orchestrator on dispatch)
result_json           NVARCHAR(MAX) -- Worker result payload (set by orchestrator)
error_message         NVARCHAR(MAX) -- Error details (set by orchestrator)
dispatched_at         DATETIME2     -- When dispatched to worker
completed_at          DATETIME2     -- When result was received
is_poll_step          BIT           -- Whether this step uses polling
poll_interval_sec     INT           -- Seconds between poll checks
poll_timeout_sec      INT           -- Max seconds before poll timeout
poll_started_at       DATETIME2     -- When polling began
last_polled_at        DATETIME2     -- Last poll check time
poll_count            INT           -- Number of poll checks performed
```

### init_executions (read/write)

```
id                    INT           -- PK
batch_id              INT           -- FK to batches
step_name             NVARCHAR      -- Step name
step_index            INT           -- Order within init sequence (0-based)
runbook_version       INT           -- Which runbook version created this
worker_id             NVARCHAR      -- Target worker pool
function_name         NVARCHAR      -- Function to execute
params_json           NVARCHAR(MAX) -- Resolved parameters as JSON
status                NVARCHAR      -- Same state machine as step_executions
job_id                NVARCHAR      -- Worker job ID
result_json           NVARCHAR(MAX) -- Worker result
error_message         NVARCHAR(MAX) -- Error details
dispatched_at         DATETIME2     -- Dispatch time
completed_at          DATETIME2     -- Completion time
is_poll_step          BIT           -- Whether this uses polling
poll_interval_sec     INT           -- Poll interval
poll_timeout_sec      INT           -- Poll timeout
poll_started_at       DATETIME2     -- When polling began
last_polled_at        DATETIME2     -- Last poll time
poll_count            INT           -- Poll count
```

### Dynamic Data Tables (read only)

Named `runbook_{sanitized_name}_v{version}`. Schema varies per runbook. System columns are:

```
_row_id               INT IDENTITY  -- PK
_member_key           NVARCHAR(256) -- Unique member identifier
_batch_time           DATETIME2     -- Batch time from data source
_first_seen_at        DATETIME2     -- First time this member appeared
_last_seen_at         DATETIME2     -- Last time this member was seen
_is_current           BIT           -- 1 if member is in latest query results
[query columns]       NVARCHAR(MAX) -- One column per data source query column
```

The orchestrator queries dynamic tables to resolve templates for catch-up steps and rollback steps:

```sql
SELECT * FROM [runbook_contoso_mailbox_migration_v3]
WHERE _member_key = @MemberKey AND _is_current = 1
```

## Step Execution State Machine

```
                 +----------+
                 | pending  |  (created by scheduler)
                 +----+-----+
                      |
                      | orchestrator dispatches to worker
                      v
                 +----------+
                 |dispatched|
                 +----+-----+
                      |
           +----------+----------+
           |          |          |
           v          v          v
     +---------+ +--------+ +--------+
     |succeeded| | failed | |polling |
     +---------+ +----+---+ +---+----+
                      |         |
                      |    +----+----+
                      |    |         |
                      |    v         v
                      | +-------+ +-----------+
                      | |succeed| |poll_timeout|
                      | +-------+ +-----+-----+
                      |                 |
                      |    (treated as failure)
                      v
                +------------+
                | rolled_back|  (after rollback steps complete)
                +------------+

Additional terminal state:
                +----------+
                | cancelled|  (member removed before execution)
                +----------+
```

### Status Definitions

| Status | Meaning |
|---|---|
| `pending` | Created by the scheduler. Not yet sent to a worker. |
| `dispatched` | Sent to the worker. Waiting for result. |
| `succeeded` | Worker completed the step successfully. |
| `failed` | Worker returned an error. May trigger rollback. |
| `polling` | Worker returned a "still in progress" response. Awaiting poll-check. |
| `poll_timeout` | Polling exceeded the configured timeout. Treated as failure. |
| `rolled_back` | A rollback sequence was executed for this step. |
| `cancelled` | The step was cancelled (e.g., member was removed from batch). |

## Polling Protocol

1. The scheduler creates step executions with `is_poll_step = 1`, `poll_interval_sec`, and `poll_timeout_sec` pre-populated from the runbook YAML.
2. The orchestrator dispatches the step to the worker. If the worker returns a "polling" response, the orchestrator sets `status = 'polling'`, `poll_started_at = now`, and `last_polled_at = now`.
3. The scheduler's timer function queries for polling steps where `DATEADD(SECOND, poll_interval_sec, last_polled_at) <= now` and sends a `poll-check` message.
4. On receiving `poll-check`, the orchestrator:
   a. Checks timeout: if `poll_started_at + poll_timeout_sec < now`, set status to `poll_timeout`.
   b. Otherwise, re-dispatches the step to the worker.
   c. Updates `last_polled_at` and increments `poll_count`.
5. This cycle repeats until the worker returns success, failure, or the timeout expires.

## Rollback Protocol

1. A step execution fails (worker returns error, or poll times out).
2. The orchestrator checks the step's `on_failure` field (via the step definition in the YAML).
3. If `on_failure` is set:
   a. Read the runbook's `yaml_content` from the `runbooks` table.
   b. Parse the YAML and look up `rollbacks[on_failure]` for the list of rollback steps.
   c. Load the member's data from the dynamic data table.
   d. Resolve templates in each rollback step's `params` using the member data, `_batch_id`, and `_batch_start_time`.
   e. Dispatch each rollback step to the worker in order.
   f. Update the original step's status to `rolled_back` after rollback completes.
4. If `on_failure` is not set, the step remains in `failed` status.

## Member Catch-Up Protocol

When a `member-added` message is received for an existing active batch:

1. Load the runbook YAML.
2. Determine which phases have already been dispatched or completed (`phase_executions.status IN ('dispatched', 'completed')`).
3. For each overdue phase:
   a. Load the new member's data from the dynamic data table using `_member_key` and `_is_current = 1`.
   b. For each step in the phase definition, resolve templates against the member's data.
   c. Insert new `step_executions` rows for this member.
   d. Dispatch the steps to the worker.
4. Future phases (still `pending`) will automatically include this member when the scheduler dispatches them.

This ensures late-joining members catch up on work that has already been done for other members in the batch.
