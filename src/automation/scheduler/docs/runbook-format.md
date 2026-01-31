# Runbook YAML Format Reference

## Overview

A runbook is a YAML document that defines a migration workflow. It specifies where to find migration members, how to group them into batches, what phases to execute and when, and what steps each phase contains. The scheduler parses this YAML on every timer tick to drive batch detection and phase dispatch.

Runbooks are published via the `POST /api/PublishRunbook` endpoint, which validates the YAML, versions it, and stores it in the `runbooks` SQL table.

## Top-Level Structure

```yaml
name: <string>                    # Required. Must match the name in the publish request.
description: <string>             # Optional. Human-readable description.
data_source: <DataSourceConfig>   # Required. Where to query migration member data.
init: <list of StepDefinition>    # Optional. Steps to run once per batch at detection time.
phases: <list of PhaseDefinition> # Required. At least one phase.
on_member_removed: <list of StepDefinition>  # Optional. Steps to run when a member leaves a batch.
rollbacks:                        # Optional. Named rollback sequences referenced by on_failure.
  <rollback_name>:
    - <StepDefinition>
    - <StepDefinition>
```

## data_source

Configures the external data source query that the scheduler executes every 5 minutes to discover migration members.

```yaml
data_source:
  type: <string>                  # Required. "dataverse" or "databricks".
  connection: <string>            # Required. Environment variable name containing the connection string/URL.
  warehouse_id: <string>          # Required for databricks only. Env var name for the warehouse ID.
  query: <string>                 # Required. SQL query to execute against the data source.
  primary_key: <string>           # Required. Column name that uniquely identifies each member.
  batch_time_column: <string>     # Required unless batch_time is set. Column containing the batch start time.
  batch_time: <string>            # Alternative to batch_time_column. Set to "immediate" for on-demand batching.
  multi_valued_columns:           # Optional. Columns that contain delimited or array values.
    - name: <string>              # Column name.
      format: <string>            # "semicolon_delimited", "comma_delimited", or "json_array".
```

### type

- `dataverse` -- Queries via the Dataverse TDS endpoint using `SqlConnection`. The `connection` value is an environment variable name (e.g., `DATAVERSE_CONNECTION_STRING`) whose value is a SQL Server connection string pointing to the TDS endpoint.
- `databricks` -- Queries via the Databricks SQL Statements REST API. The `connection` value is an environment variable name (e.g., `DATABRICKS_CONNECTION_STRING`) whose value is the workspace URL. The `warehouse_id` is another env var name (e.g., `DATABRICKS_WAREHOUSE_ID`).

### primary_key

The column in the query results that uniquely identifies each migration member. This value is stored as `_member_key` in the dynamic data table and in `batch_members.member_key`. It is used for member diff detection (adds and removes).

### batch_time_column vs. batch_time: immediate

**Scheduled batching** (`batch_time_column`): The query must return a column containing a parseable `DateTime` value. Members with the same batch time value are grouped into the same batch. The phase offsets are calculated relative to this time.

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

Some columns may contain multiple values packed into a single string. The scheduler normalizes these to JSON arrays during upsert into the dynamic table.

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

## phases

A list of phase definitions. Each phase fires at a specific offset relative to the batch start time. At least one phase is required.

```yaml
phases:
  - name: <string>              # Required. Unique name for this phase within the runbook.
    offset: <string>            # Required. When this phase fires, relative to batch_start_time.
    steps:                      # Required. At least one step.
      - <StepDefinition>
```

### Offset Format

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

## steps

Steps define the actual work to be dispatched to a worker. They appear inside `phases`, `init`, `on_member_removed`, and `rollbacks`.

```yaml
steps:
  - name: <string>              # Required. Human-readable step name.
    worker_id: <string>         # Required. Identifies which worker pool handles this step.
    function: <string>          # Required. The PowerShell function name to execute. Supports templates.
    params:                     # Optional. Key-value pairs passed as parameters to the function.
      ParamName: "{{ColumnName}}"
    on_failure: <string>        # Optional. Name of a rollback sequence to execute if this step fails.
    poll:                       # Optional. If present, this step uses polling (async completion).
      interval: <string>        # Required if poll is set. How often to re-check. Format: 15m, 24h, 30s.
      timeout: <string>         # Required if poll is set. Max time before giving up. Format: 24h, 7d.
```

### function

The function name supports template syntax. For example:

```yaml
function: "New-{{ObjectType}}"
```

If the member's `ObjectType` column is `EntraUser`, the resolved function name becomes `New-EntraUser`.

### params

Each parameter value is a string that may contain `{{template}}` expressions. These are resolved at phase dispatch time from the member's row in the dynamic data table.

```yaml
params:
  UserPrincipalName: "{{UserPrincipalName}}"
  DisplayName: "{{FirstName}} {{LastName}}"
  BatchId: "{{_batch_id}}"
  MigrationTime: "{{_batch_start_time}}"
```

### on_failure

References a named rollback sequence defined in the top-level `rollbacks` section. If the orchestrator detects that this step has failed, it reads the rollback steps from the YAML, resolves templates, and dispatches them to the worker.

The parser validates that any `on_failure` value corresponds to an existing key in `rollbacks`.

### poll

Some steps represent long-running operations where the worker returns a "polling" status. The scheduler and orchestrator use polling to periodically re-check completion.

```yaml
poll:
  interval: 15m    # Check every 15 minutes
  timeout: 24h     # Give up after 24 hours
```

Duration format for `interval` and `timeout`:
- `30s` -- 30 seconds
- `15m` -- 15 minutes
- `2h` -- 2 hours
- `7d` -- 7 days

These are converted to seconds internally via `ParseDurationSeconds()`.

The scheduler emits `poll-check` messages when a polling step's interval has elapsed (based on `last_polled_at`). The orchestrator re-executes the step and updates the status.

## Template Syntax

Templates use double-brace syntax: `{{variable_name}}`.

### Data Column Variables

Any column returned by the data source query can be referenced by name:

```yaml
params:
  Email: "{{UserPrincipalName}}"
  Name: "{{DisplayName}}"
```

The value is looked up from the member's row in the dynamic data table at phase dispatch time.

### Special Variables

| Variable | Value |
|---|---|
| `{{_batch_id}}` | The integer ID of the current batch (from `batches.id`) |
| `{{_batch_start_time}}` | The batch start time in ISO 8601 format (`DateTime.ToString("o")`) |

### Unresolved Templates

If a template variable is not found in the data row or special variables, it is left as-is in the output (e.g., `{{UnknownCol}}` remains literally in the string). A warning is logged.

## init

Optional. A list of steps that run once per batch when it is first detected, before any phases fire. Init steps are dispatched via a `batch-init` message and do not have per-member template resolution -- they can only use `{{_batch_id}}` and `{{_batch_start_time}}`.

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

If a runbook has no `init` section, the batch goes directly to `active` status.

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

## rollbacks

Optional. Named sequences of steps that the orchestrator can execute when a step with `on_failure` fails.

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

## Complete Example: Scheduled Runbook

```yaml
name: contoso-mailbox-migration
description: Migrates Contoso mailboxes with pre-migration notifications and post-migration validation

data_source:
  type: dataverse
  connection: DATAVERSE_CONNECTION_STRING
  query: >
    SELECT UserPrincipalName, DisplayName, FirstName, LastName,
           MigrationDate, EmailAliases, Department
    FROM contacts
    WHERE MigrationStatus = 'approved'
  primary_key: UserPrincipalName
  batch_time_column: MigrationDate
  multi_valued_columns:
    - name: EmailAliases
      format: semicolon_delimited

init:
  - name: create-batch-group
    worker_id: worker-01
    function: New-MigrationBatchGroup
    params:
      BatchId: "{{_batch_id}}"
      StartTime: "{{_batch_start_time}}"

phases:
  - name: pre-notification
    offset: T-5d
    steps:
      - name: send-advance-notice
        worker_id: worker-01
        function: Send-MigrationNotice
        params:
          UserPrincipalName: "{{UserPrincipalName}}"
          DisplayName: "{{DisplayName}}"
          MigrationDate: "{{_batch_start_time}}"
          NoticeType: "advance"

  - name: pre-migration
    offset: T-4h
    steps:
      - name: set-forwarding
        worker_id: worker-01
        function: Set-MailForwarding
        params:
          UserPrincipalName: "{{UserPrincipalName}}"
          ForwardTo: "{{UserPrincipalName}}"
        on_failure: undo-forwarding

  - name: migration
    offset: T-0
    steps:
      - name: start-mailbox-move
        worker_id: worker-01
        function: Start-MailboxMove
        params:
          UserPrincipalName: "{{UserPrincipalName}}"
          EmailAliases: "{{EmailAliases}}"
        on_failure: undo-mailbox-move
        poll:
          interval: 15m
          timeout: 24h
      - name: verify-migration
        worker_id: worker-01
        function: Test-MailboxMigration
        params:
          UserPrincipalName: "{{UserPrincipalName}}"

on_member_removed:
  - name: cancel-forwarding
    worker_id: worker-01
    function: Remove-MailForwarding
    params:
      UserPrincipalName: "{{UserPrincipalName}}"

rollbacks:
  undo-forwarding:
    - name: remove-forwarding
      worker_id: worker-01
      function: Remove-MailForwarding
      params:
        UserPrincipalName: "{{UserPrincipalName}}"

  undo-mailbox-move:
    - name: revert-move
      worker_id: worker-01
      function: Undo-MailboxMove
      params:
        UserPrincipalName: "{{UserPrincipalName}}"
    - name: alert-admin
      worker_id: worker-01
      function: Send-AdminAlert
      params:
        Subject: "Mailbox move rollback for {{UserPrincipalName}}"
```

## Complete Example: Immediate Runbook

```yaml
name: ondemand-user-provisioning
description: Provisions new users as soon as they appear in the queue

data_source:
  type: databricks
  connection: DATABRICKS_CONNECTION_STRING
  warehouse_id: DATABRICKS_WAREHOUSE_ID
  query: >
    SELECT email, display_name, department, manager_email
    FROM provisioning_queue
    WHERE status = 'pending'
  primary_key: email
  batch_time: immediate

phases:
  - name: provision
    offset: T-0
    steps:
      - name: create-entra-user
        worker_id: worker-01
        function: New-EntraUser
        params:
          UserPrincipalName: "{{email}}"
          DisplayName: "{{display_name}}"
          Department: "{{department}}"
      - name: assign-license
        worker_id: worker-01
        function: Set-UserLicense
        params:
          UserPrincipalName: "{{email}}"
      - name: send-welcome-email
        worker_id: worker-01
        function: Send-WelcomeEmail
        params:
          Email: "{{email}}"
          ManagerEmail: "{{manager_email}}"

on_member_removed:
  - name: disable-account
    worker_id: worker-01
    function: Disable-EntraUser
    params:
      UserPrincipalName: "{{email}}"
```
