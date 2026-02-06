# Orchestrator Architecture

## Overview

The orchestrator is the execution engine for the M&A Toolkit migration pipeline. It sits between the scheduler (timing/detection) and the cloud workers (PowerShell execution), managing job dispatch, result processing, and step progression.

## System Context

```
┌─────────────┐         ┌──────────────┐         ┌──────────────┐
│  Scheduler  │────────▶│  Orchestrator │────────▶│ Cloud Worker │
│  (Timer)    │  events │  (Functions)  │  jobs   │  (Container) │
└─────────────┘         └──────────────┘         └──────────────┘
                              │    ▲
                              │    │ results
                              ▼    │
                        ┌──────────────┐
                        │  SQL Database │
                        └──────────────┘
```

## Service Bus Topics

| Topic | Direction | Purpose |
|-------|-----------|---------|
| `orchestrator-events` | Scheduler → Orchestrator | Timing events, member changes |
| `worker-jobs` | Orchestrator → Worker | Job dispatch with parameters |
| `worker-results` | Worker → Orchestrator | Execution results |

## Message Types

### Inbound (from Scheduler)

| Message | Trigger | Handler |
|---------|---------|---------|
| `batch-init` | New batch with init steps | `BatchInitHandler` |
| `phase-due` | Phase due_at passed | `PhaseDueHandler` |
| `member-added` | New member joins batch | `MemberAddedHandler` |
| `member-removed` | Member leaves batch | `MemberRemovedHandler` |
| `poll-check` | Polling interval elapsed | `PollCheckHandler` |

### Outbound (to Worker)

```json
{
  "jobId": "guid",
  "batchId": 42,
  "workerId": "worker-01",
  "functionName": "New-EntraUser",
  "parameters": { "DisplayName": "...", "UserPrincipalName": "..." },
  "correlationData": {
    "stepExecutionId": 123,
    "isInitStep": false,
    "runbookName": "migration-runbook",
    "runbookVersion": 1
  }
}
```

### Inbound (from Worker)

```json
{
  "jobId": "guid",
  "status": "Success",
  "resultType": "Object",
  "result": { "complete": true, "data": {...} },
  "error": null,
  "durationMs": 1234,
  "timestamp": "2024-01-15T10:30:00Z",
  "correlationData": { ... }
}
```

## Handler Logic

### BatchInitHandler

Processes `batch-init` messages for batches that have init steps.

1. Load pending init_executions ordered by step_index
2. Dispatch first pending init step
3. Wait for ResultProcessor to advance to next step
4. After all init steps succeed: set batch status='active'
5. On any init step failure: set batch status='failed', trigger rollback

### PhaseDueHandler

Processes `phase-due` messages when a phase becomes due.

1. Load step_executions grouped by step_index
2. For each step_index (sequential):
   - If pending steps exist: dispatch all members in parallel
   - If in-progress steps exist: wait (handled by ResultProcessor)
   - If all succeeded: continue to next index
   - If any failed: stop (rollback handled by ResultProcessor)
3. After all indices complete: set phase status='completed'
4. Check if all phases complete: set batch status='completed'

### MemberAddedHandler

Processes `member-added` for late-joining members.

1. Load runbook YAML from database
2. Find overdue phases (status in dispatched, completed)
3. Load member data from dynamic table
4. For each overdue phase:
   - Resolve templates using member data
   - Create step_executions for new member
   - Dispatch first step_index

### MemberRemovedHandler

Processes `member-removed` when a member leaves the batch.

1. Cancel pending/dispatched steps: `UPDATE SET status='cancelled'`
2. Load runbook YAML, check for `on_member_removed` steps
3. If defined: load member data, resolve templates, dispatch steps

### PollCheckHandler

Processes `poll-check` for polling steps.

1. Check timeout: `poll_started_at + poll_timeout_sec < now`
   - Timed out: set status='poll_timeout', trigger rollback
2. Not timed out: re-dispatch same job, update last_polled_at

### ResultProcessor

Processes `WorkerResultMessage` from workers.

1. Match result by correlation data (stepExecutionId or initExecutionId)
2. Check polling convention: `Result.complete == false`
   - Yes: set status='polling', update poll timestamps
   - No: proceed to success/failure handling
3. On success:
   - Set status='succeeded', store result_json
   - For init: dispatch next init step or activate batch
   - For step: check phase progression, dispatch next index
4. On failure:
   - Set status='failed', store error_message
   - Trigger rollback if on_failure configured

## Execution Model

### Sequential vs Parallel

```
Init Steps:        Step 0 ──▶ Step 1 ──▶ Step 2     (sequential)

Phase Steps:
  step_index=0:    [Member A] [Member B] [Member C]  (parallel)
                         ▼
  step_index=1:    [Member A] [Member B] [Member C]  (parallel)
                         ▼
  step_index=2:    [Member A] [Member B] [Member C]  (parallel)
```

### Polling Steps

Steps with `poll` configuration enter polling mode:

```
Dispatch ──▶ {complete: false} ──▶ Wait Interval ──▶ Re-dispatch
                                          │
    ┌─────────────────────────────────────┘
    │
    └──▶ {complete: true} ──▶ Success
    │
    └──▶ Timeout ──▶ Failure + Rollback
```

## Rollback Sequences

Defined in runbook YAML:

```yaml
rollbacks:
  cleanup_user:
    - name: Remove-EntraUser
      worker_id: cloud-worker-pool-1
      function: Remove-EntraUser
      params:
        upn: "{{UPN}}"
```

Triggered on:
- Step failure (if `on_failure` specified)
- Poll timeout (if `on_failure` specified)
- Init step failure (batch-level rollback)

## Database Updates

### Tables Written

| Table | Fields Updated |
|-------|----------------|
| `batches` | status |
| `phase_executions` | status, completed_at |
| `step_executions` | status, job_id, result_json, error_message, dispatched_at, completed_at, poll_* |
| `init_executions` | status, job_id, result_json, error_message, dispatched_at, completed_at, poll_* |

### Status Values

**batches.status:**
- `active` - Batch ready for phase execution
- `completed` - All phases completed successfully
- `failed` - Init or step failure occurred

**phase_executions.status:**
- `dispatched` - Steps being processed
- `completed` - All steps succeeded
- `failed` - At least one step failed
- `skipped` - Skipped due to overdue_behavior

**step_executions.status / init_executions.status:**
- `pending` - Not yet dispatched
- `dispatched` - Job sent to worker
- `succeeded` - Worker returned success
- `failed` - Worker returned failure
- `polling` - Awaiting polling completion
- `poll_timeout` - Polling exceeded timeout
- `cancelled` - Cancelled due to member removal

## Error Handling

1. **Transient failures**: Service Bus retries with exponential backoff
2. **Permanent failures**: Dead-letter after max delivery count (10)
3. **Processing errors**: Log and throw to trigger retry
4. **Rollback failures**: Log and continue (fire-and-forget)

## Scaling Considerations

- Function App scales based on Service Bus queue depth
- Parallel step execution limited by Service Bus batch send (100 messages)
- Database connections pooled via SqlConnection
- Consider partitioning for high-volume workloads
