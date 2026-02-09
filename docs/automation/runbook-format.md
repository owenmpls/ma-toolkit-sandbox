# Runbook YAML Format Reference

## Overview

A runbook is a YAML document that defines a migration workflow. It specifies where to find migration members, how to group them into batches, what phases to execute and when, and what steps each phase contains. The scheduler parses this YAML on every timer tick to drive batch detection and phase dispatch.

Runbooks are published via the Admin API (`POST /api/runbooks`), which validates the YAML, versions it, and stores it in the `runbooks` SQL table. The YAML is parsed using YamlDotNet with `UnderscoredNamingConvention` and `IgnoreUnmatchedProperties`.

---

## Top-Level Structure

```yaml
name: <string>                    # Required. Runbook name (must be unique).
description: <string>             # Optional. Human-readable description.
data_source: <DataSourceConfig>   # Required. External data source configuration.
init: <list of StepDefinition>    # Optional. Batch-level steps run once at creation.
phases: <list of PhaseDefinition> # Required. At least one phase.
on_member_removed: <list of StepDefinition>  # Optional. Steps run when a member leaves.
retry: <RetryConfig>              # Optional. Global retry config for all steps/init.
rollbacks:                        # Optional. Named rollback sequences.
  <rollback_name>:
    - <StepDefinition>
```

---

## data_source

Configures the external data source query that the scheduler executes every 5 minutes to discover migration members.

```yaml
data_source:
  type: <string>                  # Required. "dataverse" or "databricks".
  connection: <string>            # Required. Environment variable name containing connection string/URL.
  warehouse_id: <string>          # Required for databricks only. Env var name for the warehouse ID.
  query: <string>                 # Required. SQL query to execute against the data source.
  primary_key: <string>           # Required. Column name that uniquely identifies each member.
  batch_time_column: <string>     # Required unless batch_time is set.
  batch_time: <string>            # Alternative to batch_time_column. Set to "immediate".
  multi_valued_columns:           # Optional. Columns that contain delimited or array values.
    - name: <string>
      format: <string>            # "semicolon_delimited", "comma_delimited", or "json_array".
```

### type

- `dataverse` -- Queries via the Dataverse TDS endpoint using `SqlConnection`. The `connection` value is an environment variable name (e.g., `DATAVERSE_CONNECTION_STRING`) whose value is a SQL Server connection string pointing to the TDS endpoint.
- `databricks` -- Queries via the Databricks SQL Statements REST API. The `connection` value is an environment variable name (e.g., `DATABRICKS_CONNECTION_STRING`) whose value is the workspace URL. The `warehouse_id` is another env var name (e.g., `DATABRICKS_WAREHOUSE_ID`).

### primary_key

The column in the query results that uniquely identifies each migration member. This value is stored in `batch_members.member_key`. It is used for member diff detection (adds and removes).

### batch_time_column vs. batch_time: immediate

Either `batch_time_column` or `batch_time: immediate` must be specified. They are mutually exclusive.

**Scheduled batching** (`batch_time_column`): The query must return a column containing a parseable `DateTime` value. Members with the same batch time value are grouped into the same batch. Phase offsets are calculated relative to this time.

```yaml
data_source:
  type: dataverse
  connection: DATAVERSE_CONNECTION_STRING
  query: "SELECT UserPrincipalName, MigrationDate FROM contacts WHERE status = 'approved'"
  primary_key: UserPrincipalName
  batch_time_column: MigrationDate
```

**Immediate batching** (`batch_time: immediate`): No batch time column is needed. All members returned by the query are assigned to a batch whose start time is the current UTC time rounded to the nearest 5-minute interval. Members already in an active batch for the same runbook are filtered out. Phases fire relative to this rounded time.

```yaml
data_source:
  type: databricks
  connection: DATABRICKS_CONNECTION_STRING
  warehouse_id: DATABRICKS_WAREHOUSE_ID
  query: "SELECT email, display_name FROM migration_queue WHERE processed = false"
  primary_key: email
  batch_time: immediate
```

### multi_valued_columns

Some columns may contain multiple values packed into a single string. The scheduler normalizes these to JSON arrays during serialization into `batch_members.data_json`.

| Format | Input | Stored Value |
|---|---|---|
| `semicolon_delimited` | `alias1;alias2;alias3` | `["alias1","alias2","alias3"]` |
| `comma_delimited` | `alias1,alias2,alias3` | `["alias1","alias2","alias3"]` |
| `json_array` | `["alias1","alias2"]` | `["alias1","alias2"]` (passed through) |

```yaml
multi_valued_columns:
  - name: EmailAliases
    format: semicolon_delimited
  - name: GroupMemberships
    format: comma_delimited
```

---

## phases

A list of phase definitions. Each phase fires at a specific offset relative to the batch start time. At least one phase is required.

```yaml
phases:
  - name: <string>              # Required. Unique name within the runbook.
    offset: <string>            # Required. When this phase fires, relative to batch_start_time.
    steps:                      # Required. At least one step.
      - <StepDefinition>
```

### Offset Format (T-notation)

Offsets use the `T-` notation to express time before the batch start time:

| Offset | Meaning | offset_minutes |
|---|---|---|
| `T-5d` | 5 days before batch_start_time | 7200 |
| `T-4h` | 4 hours before | 240 |
| `T-30m` | 30 minutes before | 30 |
| `T-90s` | 90 seconds before (rounded up to 2 minutes) | 2 |
| `T-0` | At exactly batch_start_time | 0 |

The offset is converted to minutes and stored as `offset_minutes` in `phase_executions`. The `due_at` is calculated as:

```
due_at = batch_start_time - offset_minutes
```

So `T-5d` with a `batch_start_time` of `2025-03-15T00:00:00Z` produces `due_at = 2025-03-10T00:00:00Z`.

Supported suffixes:

- `d` -- days (multiplied by 1440 minutes)
- `h` -- hours (multiplied by 60 minutes)
- `m` -- minutes
- `s` -- seconds (converted by rounding up to the nearest minute via `Math.Ceiling(n / 60.0)`)

Phases are evaluated as "due" when `due_at <= now` and `status = 'pending'`.

For manual batches (created via `POST /api/batches`), offsets are ignored. Phases are advanced manually via `POST /api/batches/{id}/advance`.

---

## steps

Steps define the actual work dispatched to a worker. They appear inside `phases`, `init`, `on_member_removed`, and `rollbacks`.

```yaml
steps:
  - name: <string>                    # Required. Step name (unique within parent).
    worker_id: <string>               # Required. Target worker pool ID.
    function: <string>                # Required. Function name to execute. Supports templates.
    params:                           # Optional. Key-value pairs passed to the function.
      ParamName: "{{ColumnName}}"
    output_params:                    # Optional. Maps result fields to template variables.
      TemplateVar: "resultField"
    on_failure: <string>              # Optional. Name of rollback sequence to execute on failure.
    poll:                             # Optional. Polling config for long-running operations.
      interval: <string>             # Required if poll is set. Re-invoke interval.
      timeout: <string>              # Required if poll is set. Max duration before timeout.
    retry:                            # Optional. Step-level retry override.
      max_retries: <int>
      interval: <string>
```

### function

The function name supports template syntax. For example:

```yaml
function: "New-{{ObjectType}}"
```

If the member's `ObjectType` column is `EntraUser`, the resolved function name becomes `New-EntraUser`.

### params

Each parameter value is a string that may contain `{{template}}` expressions. These are resolved at step creation time from the member's data.

```yaml
params:
  UserPrincipalName: "{{UserPrincipalName}}"
  DisplayName: "{{FirstName}} {{LastName}}"
  BatchId: "{{_batch_id}}"
  MigrationTime: "{{_batch_start_time}}"
```

### on_failure

References a named rollback sequence defined in the top-level `rollbacks` section. If the orchestrator detects that this step has failed (with retries exhausted or no retries configured), it reads the rollback steps from the YAML, resolves templates, and dispatches them to the worker.

The parser validates that any `on_failure` value corresponds to an existing key in `rollbacks`.

---

## output_params

Maps fields from a step's result to template variables available in subsequent steps. This is the mechanism for passing data between steps across phases.

```yaml
output_params:
  TemplateVariable: "resultFieldName"
```

**How it works:**

1. When a step succeeds, the orchestrator's `ResultProcessor` checks whether the step definition has `output_params` configured.
2. It parses the step's result JSON and performs a case-insensitive lookup for each specified result field name.
3. Matched values are written to `batch_members.worker_data_json` via `MergeWorkerDataAsync`, which merges new key-value pairs into any existing worker data (new values win on key collision).
4. At step creation time (`PhaseDueHandler`), the orchestrator merges `data_json` (original data source columns) with `worker_data_json` (output params from prior steps) into a single dictionary. Worker data wins on collision.
5. The merged dictionary is used for template resolution, making `{{TemplateVariable}}` available in all subsequent steps.

**Field lookup:** Case-insensitive -- `"mailboxGuid"` matches a result property `MailboxGuid`.

**Polling steps:** For steps with `poll` configured, when the worker returns `{ "complete": true, "data": {...} }`, the orchestrator extracts output params from the `data` sub-object (not the top-level result).

**Example -- two-step data flow:**

```yaml
phases:
  - name: prepare
    offset: T-5d
    steps:
      - name: get-mailbox
        worker_id: worker-01
        function: Get-MailboxInfo
        params:
          UserId: "{{UserPrincipalName}}"
        output_params:
          MailboxGuid: "mailboxGuid"          # result.mailboxGuid -> {{MailboxGuid}}
          PrimarySmtp: "primarySmtpAddress"   # result.primarySmtpAddress -> {{PrimarySmtp}}

      - name: set-guids
        worker_id: worker-01
        function: Set-ExchangeMailUserGuids
        params:
          Identity: "{{TargetUPN}}"
          ExchangeGuid: "{{MailboxGuid}}"     # Populated by get-mailbox output_params
```

**Cross-phase usage:** Because output params are persisted in `worker_data_json`, they remain available across subsequent phases -- not just within the same phase.

---

## retry

Configurable retry logic for transient failures. Two levels of configuration are supported:

### Global retry

A top-level `retry` key applies to all steps and init steps as a default:

```yaml
retry:
  max_retries: <int>        # Maximum number of retry attempts.
  interval: <string>        # Delay between retries (duration format: 30s, 5m, 1h, 1d).
```

### Step-level retry

Individual steps can override the global retry config entirely:

```yaml
steps:
  - name: flaky-step
    retry:
      max_retries: 3
      interval: 30s
  - name: no-retry-step
    retry:
      max_retries: 0          # Explicitly disables retry even with global config.
```

### Resolution rules

- Step-level `retry` replaces global `retry` entirely (not merged). The effective retry config is `step.Retry ?? definition.Retry`.
- The effective retry config is resolved once at step creation time and stored on the execution record (`max_retries`, `retry_interval_sec`, `retry_count`, `retry_after`).
- Steps with no retry config (and no global config) fail immediately with no retry.

### Retry flow

1. Step fails -- `ResultProcessor` marks it `failed` via `SetFailedAsync`.
2. If `max_retries` is set and `retry_count < max_retries`: `SetRetryPendingAsync` resets status to `pending`, increments `retry_count`, sets `retry_after`.
3. `RetryScheduler` sends a scheduled `retry-check` message to the `orchestrator-events` topic with `ScheduledEnqueueTime` set to the retry interval.
4. When the message arrives, `RetryCheckHandler` verifies the step is still `pending` (it may have been cancelled between scheduling and arrival), then re-dispatches via `WorkerDispatcher`.

### Job ID format

Retry job IDs follow the pattern `step-{id}-retry-{retryCount}` (or `init-{id}-retry-{retryCount}` for init steps). This ensures uniqueness for Service Bus message deduplication.

### What does NOT retry

- **Poll timeout** -- Polling steps already manage their own duration via `poll.timeout`. A poll timeout does not trigger retry, as retrying would silently extend the configured timeout.
- **Steps with `max_retries: 0`** -- Explicit opt-out even when global retry is configured.
- **Steps with no retry config** (and no global config) -- Immediate failure, then rollback (if `on_failure` is set) and member failure.

---

## poll

For long-running operations that span multiple job cycles. Steps with `poll` configured are dispatched like normal steps, but the worker signals whether the operation is still in progress.

```yaml
poll:
  interval: <string>     # Required. Re-invoke interval (duration format).
  timeout: <string>      # Required. Max duration before giving up (duration format).
```

Duration format for `interval` and `timeout`:

- `30s` -- 30 seconds
- `15m` -- 15 minutes
- `2h` -- 2 hours
- `7d` -- 7 days

These are converted to seconds internally via `ParseDurationSeconds()`.

### Polling convention

The worker communicates progress via the result structure:

- **Still in progress:** `{ "complete": false }` -- The orchestrator marks the step as `polling` and the scheduler emits `poll-check` messages at the configured interval (based on `last_polled_at`).
- **Complete:** `{ "complete": true, "data": {...} }` -- The orchestrator marks the step as `succeeded`. If `output_params` are configured, they are extracted from the `data` sub-object.
- **Timeout:** If `poll.timeout` elapses, the step status becomes `poll_timeout`. This is treated as a failure -- it triggers rollback if `on_failure` is set and marks the member as failed. Poll timeout does **not** trigger retry.

---

## Template Syntax

Templates use double-brace syntax: `{{variable_name}}`.

### Data column variables

Any column returned by the data source query can be referenced by name:

```yaml
params:
  Email: "{{UserPrincipalName}}"
  Name: "{{DisplayName}}"
```

The value is looked up from the member's data at step creation time.

### Worker output variables

Variables produced by `output_params` from previous steps are also available. At step creation time, the orchestrator merges the member's original data (`data_json`) with accumulated worker output data (`worker_data_json`). Worker data takes precedence on key collision.

### Special variables

| Variable | Value |
|---|---|
| `{{_batch_id}}` | The integer ID of the current batch (from `batches.id`). |
| `{{_batch_start_time}}` | The batch start time in ISO 8601 format (`DateTime.ToString("o")`). |

### Unresolved variables

If a template variable cannot be resolved from the member data, worker data, or special variables, a `TemplateResolutionException` is thrown. For phase steps, this causes the member to be skipped during step creation (a warning is logged). For init steps, which only have access to `{{_batch_id}}` and `{{_batch_start_time}}`, any non-special variable reference will raise this exception.

The parser also validates template syntax at publish time -- unclosed `{{` or unmatched `}}` are flagged as validation errors.

---

## init

Optional. A list of steps that run once per batch when it is first detected, before any phases fire. Init steps do not have per-member template resolution -- they can only use `{{_batch_id}}` and `{{_batch_start_time}}`.

```yaml
init:
  - name: create-distribution-group
    worker_id: worker-01
    function: New-DistributionGroup
    params:
      GroupName: "batch-{{_batch_id}}-dl"
      Description: "Migration batch starting {{_batch_start_time}}"
  - name: wait-for-group-sync
    worker_id: worker-01
    function: Test-DistributionGroupSync
    params:
      GroupName: "batch-{{_batch_id}}-dl"
    poll:
      interval: 5m
      timeout: 1h
```

Init steps execute sequentially (one at a time). The `ResultProcessor` dispatches the next init step after the current one succeeds. If any init step fails (with retries exhausted), the batch status becomes `failed`.

If a runbook has no `init` section, the batch goes directly to `active` status.

Init steps support `retry`, `poll`, and `on_failure` -- the same fields as phase steps.

---

## on_member_removed

Optional. Steps to execute when a member is removed from a batch (i.e., they no longer appear in the data source query results). These steps are dispatched via a `member-removed` message.

```yaml
on_member_removed:
  - name: disable-forwarding
    worker_id: worker-01
    function: Remove-MailForwarding
    params:
      UserPrincipalName: "{{UserPrincipalName}}"
```

Member removal steps have access to the removed member's data for template resolution.

---

## rollbacks

Optional. Named sequences of steps that the orchestrator executes when a step with `on_failure` fails (after retries are exhausted).

```yaml
rollbacks:
  undo-mailbox-move:
    - name: revert-mailbox
      worker_id: worker-01
      function: Undo-MailboxMove
      params:
        UserPrincipalName: "{{UserPrincipalName}}"
    - name: notify-admin
      worker_id: worker-01
      function: Send-AdminAlert
      params:
        Subject: "Rollback triggered for {{UserPrincipalName}}"
```

Referenced by a step's `on_failure` field. Rollback steps execute fire-and-forget (no status tracking). Rollback steps have access to the same template variables as the failed step, including the member's data and any accumulated worker output data.

---

## overdue_behavior

Set at publish time via the Admin API (not in the YAML itself). Controls behavior during runbook version transitions when existing active batches get new phase executions:

- `rerun` (default) -- Re-execute past-due phases during version transitions.
- `ignore` -- Skip past-due phases (mark as `skipped`).

---

## Validation Rules

The following rules are enforced at publish time by the `RunbookParser.Validate()` method:

- `name` is required.
- `data_source` is required with valid `type`, `connection`, `query`, and `primary_key`.
- Either `batch_time_column` or `batch_time: immediate` must be specified.
- `warehouse_id` is required when `type` is `databricks`.
- `multi_valued_columns` formats must be one of: `semicolon_delimited`, `comma_delimited`, `json_array`.
- At least one phase is required. Phase names must be unique.
- Each phase must have a valid `name`, `offset`, and at least one step.
- Offsets must match the `T-{value}{unit}` format (or `T-0`).
- Step names must be unique within their parent phase.
- Each step requires `name`, `worker_id`, and `function`.
- `on_failure` must reference an existing key in `rollbacks`.
- `poll.interval` and `poll.timeout` are both required if `poll` is present, and must be parseable durations.
- `output_params` keys and values must not be empty.
- Template syntax in params is validated for balanced `{{` / `}}` braces.

---

## Complete Example: Scheduled Runbook

A mailbox migration with data source discovery, multiple phases with offsets, output params, polling, retry, and rollbacks.

```yaml
name: contoso-mailbox-migration
description: Migrates Exchange mailboxes from Contoso to target tenant

data_source:
  type: dataverse
  connection: DATAVERSE_CONNECTION_STRING
  query: >
    SELECT UserPrincipalName, DisplayName, Department,
           TargetUPN, ProxyAddresses, MigrationDate
    FROM migration_wave
    WHERE Status = 'Approved'
  primary_key: UserPrincipalName
  batch_time_column: MigrationDate
  multi_valued_columns:
    - name: ProxyAddresses
      format: semicolon_delimited

retry:
  max_retries: 2
  interval: 5m

init:
  - name: validate-capacity
    worker_id: worker-01
    function: Test-MigrationCapacity
    params:
      BatchId: "{{_batch_id}}"

phases:
  - name: preparation
    offset: T-5d
    steps:
      - name: create-mail-user
        worker_id: worker-01
        function: New-EntraUser
        params:
          DisplayName: "{{DisplayName}}"
          UserPrincipalName: "{{TargetUPN}}"
          MailNickname: "{{UserPrincipalName}}"
        output_params:
          TargetUserId: "Id"
        on_failure: cleanup_user

      - name: set-guids
        worker_id: worker-01
        function: Set-ExchangeMailUserGuids
        params:
          Identity: "{{TargetUPN}}"
          ExchangeGuid: "{{MailboxGuid}}"

  - name: migration
    offset: T-0
    steps:
      - name: start-move
        worker_id: worker-01
        function: Start-MailboxMove
        params:
          SourceUser: "{{UserPrincipalName}}"
          TargetUser: "{{TargetUPN}}"
        poll:
          interval: 5m
          timeout: 8h
        output_params:
          MoveStatus: "status"
        retry:
          max_retries: 3
          interval: 10m

  - name: post-migration
    offset: T-0
    steps:
      - name: verify-mail
        worker_id: worker-01
        function: Test-ExchangeAttributeMatch
        params:
          Identity: "{{TargetUPN}}"
          AttributeName: PrimarySmtpAddress
          ExpectedValue: "{{UserPrincipalName}}"

rollbacks:
  cleanup_user:
    - name: remove-user
      worker_id: worker-01
      function: Remove-EntraUser
      params:
        UserId: "{{TargetUserId}}"
```

---

## Complete Example: Immediate Runbook

User provisioning with immediate batching, init steps, member removal cleanup, and rollbacks.

```yaml
name: user-provisioning
description: Provisions users from approved requests immediately

data_source:
  type: databricks
  connection: DATABRICKS_CONNECTION_STRING
  warehouse_id: DATABRICKS_WAREHOUSE_ID
  query: >
    SELECT user_id, email, display_name, department, manager_id
    FROM provisioning_queue
    WHERE status = 'approved'
  primary_key: user_id
  batch_time: immediate

phases:
  - name: create-user
    offset: T-0
    steps:
      - name: create-account
        worker_id: worker-01
        function: New-EntraUser
        params:
          DisplayName: "{{display_name}}"
          UserPrincipalName: "{{email}}"
          MailNickname: "{{user_id}}"
        output_params:
          NewUserId: "Id"
        on_failure: cleanup

      - name: add-to-group
        worker_id: worker-01
        function: Add-EntraGroupMember
        params:
          GroupId: "dept-{{department}}"
          UserId: "{{NewUserId}}"

on_member_removed:
  - name: disable-user
    worker_id: worker-01
    function: Set-EntraUserEnabled
    params:
      UserId: "{{email}}"
      Enabled: "false"

rollbacks:
  cleanup:
    - name: remove-account
      worker_id: worker-01
      function: Remove-EntraUser
      params:
        UserId: "{{NewUserId}}"
```
