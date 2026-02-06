# Admin API Usage Guide

## Prerequisites

- An Entra ID app registration with `Admin` and `Reader` app roles defined
- Users/groups assigned to the appropriate app roles in the Enterprise Application
- The API deployed to Azure (or running locally with Azure Functions Core Tools)
- A valid JWT token for authentication

## Authentication

All requests require a valid Entra ID JWT token in the `Authorization` header:

```
Authorization: Bearer eyJ0eXAiOiJKV1Q...
```

### Obtaining a Token

**For the CLI** (interactive users):
```bash
matoolkit auth login
```

**For scripts/automation** (client credentials):
```bash
TOKEN=$(az account get-access-token \
  --resource api://YOUR_CLIENT_ID \
  --query accessToken -o tsv)

curl -H "Authorization: Bearer $TOKEN" \
  https://your-api.azurewebsites.net/api/runbooks
```

**For testing** (device code flow):
```bash
# Using Azure CLI
az login --scope api://YOUR_CLIENT_ID/.default
TOKEN=$(az account get-access-token --resource api://YOUR_CLIENT_ID --query accessToken -o tsv)
```

### Role Requirements

| Role | Access |
|------|--------|
| `Admin` | Full access — publish runbooks, create/advance/cancel batches, manage automation, add/remove members |
| `Reader` | Read-only — list/get runbooks, view batches, download templates, view phases/steps/members |

## API Reference

### Runbook Management

#### Publish a Runbook

```http
POST /api/runbooks
Content-Type: application/json

{
  "name": "my-migration",
  "yamlContent": "name: my-migration\ndata_source:\n  ...",
  "overdueBehavior": "rerun",
  "rerunInit": false
}
```

**Response** (200):
```json
{
  "runbookId": 1,
  "version": 1,
  "dataTableName": "runbook_my_migration_v1"
}
```

Each publish creates a new version and deactivates previous versions. The `name` in the request body must match the `name` field in the YAML.

#### List Active Runbooks

```http
GET /api/runbooks
```

**Response** (200):
```json
{
  "runbooks": [
    {
      "id": 1,
      "name": "my-migration",
      "version": 3,
      "dataTableName": "runbook_my_migration_v3",
      "overdueBehavior": "rerun",
      "rerunInit": false,
      "createdAt": "2025-06-15T10:00:00Z"
    }
  ]
}
```

#### Get a Runbook

```http
GET /api/runbooks/{name}
GET /api/runbooks/{name}/versions/{version}
```

Returns the full runbook including YAML content.

#### List All Versions

```http
GET /api/runbooks/{name}/versions
```

Returns all versions (active and inactive) for a runbook name.

#### Deactivate a Version

```http
DELETE /api/runbooks/{name}/versions/{version}
```

Soft-deletes a version by setting `is_active = 0`. Returns 409 if already inactive.

### Automation Control

#### Get Automation Status

```http
GET /api/runbooks/{name}/automation
```

**Response** (200):
```json
{
  "runbookName": "my-migration",
  "automationEnabled": true,
  "enabledAt": "2025-06-15T10:00:00Z",
  "enabledBy": "admin@contoso.com",
  "disabledAt": null,
  "disabledBy": null
}
```

#### Enable/Disable Automation

```http
PUT /api/runbooks/{name}/automation
Content-Type: application/json

{
  "enabled": true
}
```

When automation is enabled, the scheduler will query the runbook's data source on its 5-minute timer and create batches automatically. Disabling automation stops new batch creation but does not affect in-progress batches.

The `enabledBy`/`disabledBy` fields are set from the caller's JWT identity.

### Query Preview

```http
POST /api/runbooks/{name}/query/preview
```

Executes the runbook's data source query and returns results without creating a batch.

**Response** (200):
```json
{
  "rowCount": 150,
  "columns": ["user_id", "email", "department", "migration_date"],
  "sample": [
    {
      "user_id": "user1",
      "email": "user1@contoso.com",
      "department": "Engineering",
      "migration_date": "2025-07-01"
    }
  ],
  "batchGroups": [
    { "batchTime": "2025-07-01", "memberCount": 50 },
    { "batchTime": "2025-07-08", "memberCount": 100 }
  ]
}
```

### CSV Template

```http
GET /api/runbooks/{name}/template
```

Returns a CSV file with the correct headers and a sample row showing expected formats. Use this template to prepare data for manual batch creation.

**Response** (200, `text/csv`):
```csv
user_id,email,department,proxy_addresses
user1,user1@contoso.com,Engineering,addr1;addr2
```

### Batch Management

#### List Batches

```http
GET /api/batches
GET /api/batches?runbookId=1&status=active&manual=true
```

Query parameters are all optional filters.

#### Get Batch Details

```http
GET /api/batches/{id}
```

Returns the batch record, phase executions, and init step executions.

#### Create a Manual Batch

```http
POST /api/batches
Content-Type: multipart/form-data

runbookName: my-migration
file: @members.csv
```

The CSV file must include the primary key column defined in the runbook. Duplicate keys are rejected. The batch is created with `is_manual=1` and `created_by` set to the caller's identity.

**Response** (200):
```json
{
  "batchId": 123,
  "status": "pending_init",
  "memberCount": 25,
  "availablePhases": ["preparation", "migration", "cleanup"],
  "warnings": []
}
```

#### Advance a Manual Batch

```http
POST /api/batches/{id}/advance
```

Advances the batch to the next step:

1. **First call** — dispatches init steps (if defined). Status becomes `init_in_progress`.
2. **After init completes** — dispatches the first phase. Status becomes `active`.
3. **After each phase** — dispatches the next phase.
4. **After all phases** — returns success with `action: "all_phases_complete"`.

**Response** (200):
```json
{
  "action": "phase_dispatched",
  "phaseName": "migration",
  "memberCount": 25,
  "stepCount": 2,
  "nextPhase": "cleanup"
}
```

Only works on manual batches (`is_manual=1`). Returns 400 if init is still in progress or if the batch is not manual.

#### Cancel a Batch

```http
POST /api/batches/{id}/cancel
```

Sets batch status to `failed`. Cannot cancel already completed or failed batches.

### Member Management

#### List Members

```http
GET /api/batches/{id}/members
```

#### Add Members

```http
POST /api/batches/{id}/members
Content-Type: multipart/form-data

file: @new-members.csv
```

Only works on manual batches. Duplicate member keys (already in the batch) are skipped.

#### Remove a Member

```http
DELETE /api/batches/{id}/members/{memberId}
```

Soft-removes the member (sets `removed_at` timestamp). Only works on manual batches.

### Execution Tracking

#### List Phase Executions

```http
GET /api/batches/{id}/phases
```

Returns phases ordered by offset_minutes.

#### List Step Executions

```http
GET /api/batches/{id}/steps
```

Returns all step executions across all phases for the batch.

## Error Responses

All errors follow a consistent format:

```json
{
  "error": "Description of what went wrong"
}
```

| Status | Meaning |
|--------|---------|
| 400 | Bad request — invalid input, validation failure, or illegal state transition |
| 401 | Unauthorized — missing or invalid JWT token |
| 403 | Forbidden — valid token but insufficient role |
| 404 | Not found — runbook or batch doesn't exist |
| 409 | Conflict — resource already in the requested state (e.g., already inactive) |

## Common Workflows

### Setting Up a New Migration Runbook

1. Write the runbook YAML definition
2. `POST /api/runbooks` — publish the runbook
3. `POST /api/runbooks/{name}/query/preview` — verify the query returns expected data
4. `GET /api/runbooks/{name}/template` — download the CSV template
5. Test with a manual batch before enabling automation

### Running a Manual Migration

1. `GET /api/runbooks/{name}/template` — download template
2. Fill in the CSV with member data
3. `POST /api/batches` — create the batch
4. `POST /api/batches/{id}/advance` — dispatch init steps
5. Wait for init, then `POST /api/batches/{id}/advance` — dispatch each phase
6. Monitor with `GET /api/batches/{id}/phases` and `GET /api/batches/{id}/steps`

### Enabling Automated Batch Creation

1. Ensure the runbook is published and the query returns correct data
2. `PUT /api/runbooks/{name}/automation` with `{"enabled": true}`
3. The scheduler will now query the data source every 5 minutes and create batches automatically
4. Monitor with `GET /api/batches?runbookId={id}`
