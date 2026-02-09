# Admin API Developer Guide

## Overview

The Admin API is a C# Azure Functions project (isolated worker, .NET 8) that provides RESTful endpoints for managing the automation subsystem. It handles runbook definitions, automation control, batch management, CSV uploads, and execution monitoring.

All endpoints require Entra ID authentication via JWT bearer tokens (`Microsoft.Identity.Web`). Write operations require the `Admin` app role; read operations require any authenticated user.

## Authentication

### Bearer Token

All requests must include an `Authorization: Bearer <token>` header with a valid Entra ID JWT token.

### Obtaining Tokens

**CLI (matoolkit):**
```bash
matoolkit auth login    # Device code flow, tokens cached persistently
```

**Scripts / Automation:**
```bash
az account get-access-token --resource api://YOUR_CLIENT_ID --query accessToken -o tsv
```

**Testing:**
```bash
TOKEN=$(az account get-access-token --resource api://YOUR_CLIENT_ID --query accessToken -o tsv)
curl -H "Authorization: Bearer $TOKEN" https://your-api.azurewebsites.net/api/runbooks
```

### Roles

| Role | Access |
|------|--------|
| `Admin` | Full access — all read and write operations |
| Any authenticated user | Read-only — GET endpoints only |

Roles are assigned in the Entra ID Enterprise Application for the API's app registration.

### User Identity

The API extracts the caller's identity from JWT claims using the fallback chain: `preferred_username` -> `name` -> `oid` -> `"system"`. Used to record who performed actions (e.g., `created_by` on batches, `enabled_by` on automation settings).

## Endpoint Reference

### Runbook Management

#### Publish Runbook
```
POST /api/runbooks
Content-Type: application/json
Authorization: Bearer <token>
```

**Request body:**
```json
{
  "name": "my-migration",
  "yaml_content": "name: my-migration\ndescription: ...\n...",
  "overdue_behavior": "rerun",
  "rerun_init": false
}
```

- `overdue_behavior`: `"rerun"` (default) or `"ignore"` -- controls how past-due phases are handled during version transitions
- `rerun_init`: whether to re-run init steps when a new version supersedes an active batch

**Response** (200):
```json
{
  "id": 1,
  "name": "my-migration",
  "version": 1,
  "isActive": true,
  "createdAt": "2025-01-15T10:00:00Z"
}
```

#### List Runbooks
```
GET /api/runbooks
```
Returns all active runbooks (latest active version per name).

#### Get Runbook
```
GET /api/runbooks/{name}
```
Returns the latest active version of the named runbook.

#### List Versions
```
GET /api/runbooks/{name}/versions
```
Returns all versions of the named runbook.

#### Get Specific Version
```
GET /api/runbooks/{name}/versions/{v}
```

#### Deactivate Version
```
DELETE /api/runbooks/{name}/versions/{v}
```
Sets `IsActive = false`. Returns 409 if already inactive.

### Automation Control

#### Get Automation Status
```
GET /api/runbooks/{name}/automation
```

**Response:**
```json
{
  "runbookName": "my-migration",
  "automationEnabled": true,
  "enabledAt": "2025-01-15T10:00:00Z",
  "enabledBy": "admin@contoso.com"
}
```

#### Enable/Disable Automation
```
PUT /api/runbooks/{name}/automation
Content-Type: application/json
```

**Request body:**
```json
{
  "enabled": true
}
```

When enabled, the scheduler queries the data source every 5 minutes and creates batches automatically. Disabling only stops new batch creation -- existing batches continue processing.

### Query Preview

#### Preview Query Results
```
POST /api/runbooks/{name}/query/preview
```

Executes the runbook's data source query and returns results without creating batches or side effects. Shows how members would be grouped into batches by the `batch_time_column`.

**Response:**
```json
{
  "totalRows": 150,
  "sampleRows": [...],
  "batchGroups": [
    { "batchTime": "2025-02-01T00:00:00Z", "memberCount": 50 },
    { "batchTime": "2025-02-15T00:00:00Z", "memberCount": 100 }
  ]
}
```

### CSV Template

#### Download Template
```
GET /api/runbooks/{name}/template
```

Returns a CSV file with column headers extracted from the runbook's query. The sample row shows multi-valued column formats (e.g., semicolon-delimited values).

**Response** (200, text/csv):
```csv
user_id,email,department,proxy_addresses
user1,user1@contoso.com,Engineering,addr1;addr2
```

### Batch Management

#### List Batches
```
GET /api/batches
GET /api/batches?runbookId=1&status=active&manual=true
```
All query parameters are optional filters.

#### Create Manual Batch
```
POST /api/batches
Content-Type: multipart/form-data

runbookName: my-migration
file: @members.csv
```

The CSV must include the primary key column defined in the runbook. Duplicate keys are rejected. The batch is created with `is_manual=1` and `created_by` set to the caller's identity.

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

#### Get Batch Details
```
GET /api/batches/{id}
```
Returns the batch record, phase executions, and init step executions.

#### Advance Manual Batch
```
POST /api/batches/{id}/advance
```

Advances the batch to the next step:
1. **First call** -- dispatches init steps (if defined). Status becomes `init_in_progress`.
2. **After init completes** -- dispatches the first phase. Status becomes `active`.
3. **After each phase completes** -- dispatches the next phase.
4. **After all phases complete** -- returns `action: "all_phases_complete"`.

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

Only works on manual batches. Returns 400 if init is still in progress or batch is not manual.

#### Cancel Batch
```
POST /api/batches/{id}/cancel
```
Sets batch status to `failed`. Cannot cancel completed or already-failed batches.

### Member Management

#### List Members
```
GET /api/batches/{id}/members
```

#### Add Members
```
POST /api/batches/{id}/members
Content-Type: multipart/form-data

file: @new-members.csv
```
Only works on manual batches. Duplicate member keys (already in the batch) are skipped.

#### Remove Member
```
DELETE /api/batches/{id}/members/{memberId}
```
Soft-removes the member (sets `removed_at` timestamp). Only works on manual batches.

### Execution Tracking

#### List Phase Executions
```
GET /api/batches/{id}/phases
```
Returns phases ordered by offset_minutes.

#### List Step Executions
```
GET /api/batches/{id}/steps
```
Returns all step executions across all phases for the batch.

## CSV Format Requirements

- Must include the primary key column defined in the runbook's `data_source.primary_key`
- Values containing commas must be quoted with double quotes
- Duplicate primary key values within the same CSV are rejected
- Multi-valued columns use the format defined in the runbook (semicolon-delimited, comma-delimited, or JSON array)

## Error Responses

All errors follow a consistent format:

```json
{
  "error": "Description of what went wrong"
}
```

| Status | Meaning |
|--------|---------|
| 400 | Bad request -- invalid input, validation failure, or illegal state transition |
| 401 | Unauthorized -- missing or invalid JWT token |
| 403 | Forbidden -- valid token but insufficient role |
| 404 | Not found -- runbook or batch doesn't exist |
| 409 | Conflict -- resource already in requested state (e.g., already inactive) |

## Common Workflows

### Setting Up a New Migration Runbook

1. Write the runbook YAML definition
2. `POST /api/runbooks` -- publish the runbook
3. `POST /api/runbooks/{name}/query/preview` -- verify the query returns expected data
4. `GET /api/runbooks/{name}/template` -- download the CSV template
5. Test with a manual batch before enabling automation

### Running a Manual Migration

1. `GET /api/runbooks/{name}/template` -- download template
2. Fill in the CSV with member data
3. `POST /api/batches` -- create the batch
4. `POST /api/batches/{id}/advance` -- dispatch init steps
5. Wait for init, then `POST /api/batches/{id}/advance` -- dispatch each phase
6. Monitor with `GET /api/batches/{id}/phases` and `GET /api/batches/{id}/steps`

### Enabling Automated Batch Creation

1. Ensure the runbook is published and the query returns correct data
2. `PUT /api/runbooks/{name}/automation` with `{"enabled": true}`
3. The scheduler will now query the data source every 5 minutes and create batches automatically
4. Monitor with `GET /api/batches?runbookId={id}`
