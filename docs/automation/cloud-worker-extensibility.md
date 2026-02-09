# Cloud Worker Functions & Extensibility

## Overview

The cloud worker is a containerized PowerShell 7.4 application running in Azure Container Apps. It receives job messages from a Service Bus topic, executes migration functions against a target Microsoft 365 tenant (Entra ID via Microsoft Graph and Exchange Online), and reports results back via a results topic. It supports parallel job execution via a managed RunspacePool with per-runspace authenticated sessions.

## Architecture

### Boot Sequence (8 Phases)

1. **Configuration** -- Load and validate environment variables
2. **Logging** -- Initialize Application Insights TelemetryClient (with console fallback)
3. **Azure Auth** -- Connect to Azure using managed identity (DefaultAzureCredential) for infrastructure access (Key Vault, Service Bus)
4. **Certificate Retrieval** -- Retrieve PFX certificate from Key Vault for target tenant authentication
5. **Service Bus** -- Load `Azure.Messaging.ServiceBus` .NET assemblies directly (Az.ServiceBus is management-plane only), create client/receiver/sender
6. **Module Import** -- Import `StandardFunctions` module and discover custom modules from `CustomFunctions/`
7. **RunspacePool** -- Create pool and authenticate each runspace to MgGraph and EXO using the PFX certificate
8. **Job Dispatcher** -- Register SIGTERM/SIGINT shutdown handlers, start health check HTTP server on port 8080, enter main processing loop

### Scale-to-Zero

The worker follows a scale-to-zero pattern managed by Azure Container Apps and KEDA:

- ACA is configured with **min replicas = 0** and **max replicas = 1**.
- A KEDA `azure-servicebus` scaler monitors the worker's subscription on the `worker-jobs` topic. When one or more messages are pending, KEDA scales the container from 0 to 1, triggering the full boot sequence.
- The worker tracks idle time -- the elapsed duration since the last message was received or last job completed.
- When no jobs are in flight and the idle timeout is reached (`IDLE_TIMEOUT_SEC`, default 300 seconds), the worker initiates a graceful shutdown and the process exits. ACA then scales back to 0.
- Set `IDLE_TIMEOUT_SEC=0` to disable idle shutdown and keep the worker running indefinitely.

```
Messages arrive on subscription
    |
    v
KEDA detects messageCount >= 1
    |
    v
ACA scales 0 -> 1 (container starts)
    |
    v
Worker boots, authenticates, processes jobs
    |
    v
No new messages for IDLE_TIMEOUT_SEC seconds
    |
    v
Worker exits gracefully, ACA scales 1 -> 0
```

### Shutdown Sequence

Shutdown is triggered by either a SIGTERM from the container orchestrator or by the idle timeout:

1. Shutdown signal received (SIGTERM or idle timeout reached)
2. Stop accepting new messages
3. Wait for active jobs to complete (`GRACE_PERIOD_SEC`, default 30 seconds)
4. Send remaining results
5. Close runspace pool
6. Dispose Service Bus resources
7. Flush telemetry
8. Exit (process exits, ACA scales to zero)

### Parallelism Model

The worker uses a PowerShell `RunspacePool` for parallel job execution. This was chosen over alternatives for the following reasons:

- **RunspacePool vs ThreadJobs**: RunspacePool provides lower overhead and more control over the execution environment. ThreadJobs use the PowerShell Jobs infrastructure which adds scheduling overhead inappropriate for a high-throughput worker.
- **RunspacePool vs ForEach-Object -Parallel**: The `-Parallel` parameter is designed for pipeline-based parallel iteration, not for a long-lived worker pool accepting work items over time.

Each runspace in the pool maintains its own authenticated sessions:

- **Microsoft Graph**: `Connect-MgGraph -Certificate` with the app's X509 certificate per runspace. The MgGraph module stores connection context in module-scoped variables, so each runspace needs its own connection.
- **Exchange Online**: `Connect-ExchangeOnline -Certificate` with native certificate-based app-only auth via splatting (using `-ShowBanner:$false` directly causes parse issues in scriptblocks). Each runspace gets its own EXO session.

This isolation prevents thread-safety issues that would arise from sharing a single connection across concurrent operations.

The `MAX_PARALLELISM` configuration controls the runspace pool size. The job dispatcher only fetches new Service Bus messages when runspace slots are available, providing natural backpressure.

### Authentication

**Infrastructure access (managed identity):**
The container app has a system-assigned managed identity used for Azure Key Vault secret retrieval and Service Bus data operations (send/receive). RBAC role assignments are provisioned by the Bicep template.

**Target tenant access (app registration):**
A separate Entra ID app registration in the target tenant provides access to Graph and Exchange Online. The PFX certificate is stored as a Key Vault Certificate, and the worker retrieves it via the associated secret at startup.

- `Connect-MgGraph -Certificate` for Microsoft Graph
- `Connect-ExchangeOnline -Certificate` for Exchange Online
- Required Graph permissions: `User.ReadWrite.All`, `Group.ReadWrite.All`, `Directory.ReadWrite.All`
- Required Exchange permissions: Application permission via app role or admin consent

**Certificate handling:**
The certificate PFX is exported to `[byte[]]` once at startup and passed to each runspace. Byte arrays serialize cleanly across runspace boundaries, whereas `X509Certificate2` holds a private key handle that may not survive cross-runspace transfer reliably. Each runspace reconstructs the `X509Certificate2` from bytes using the `EphemeralKeySet` flag (critical for Linux containers -- avoids writing private keys to disk).

Both `Connect-MgGraph` and `Connect-ExchangeOnline` handle token lifecycle internally when using certificate auth, eliminating the need for manual token refresh logic.

```
Worker Container (Azure tenant A)
    |
    +-- Managed Identity --> Key Vault (tenant A)
    |                            +-- Certificate (PFX)
    |
    +-- Managed Identity --> Service Bus (tenant A)
    |
    +-- App Registration --> Target Tenant (tenant B)
        +-- Graph API      (Connect-MgGraph -Certificate)
        +-- Exchange Online (Connect-ExchangeOnline -Certificate)
```

### Service Bus Integration

**Topics and subscriptions:**

- **Jobs Topic** (`worker-jobs`): The orchestrator publishes job messages. Each worker has a subscription with a SQL filter: `WorkerId = 'worker-XX'`.
- **Results Topic** (`worker-results`): Workers publish result messages. The orchestrator has a subscription receiving all results.

**Message handling:**

- Messages are received with **PeekLock** mode. The worker completes the message after successful processing or sends a failure result. Messages are abandoned on infrastructure errors so they can be retried.
- Messages include a `WorkerId` application property. The subscription SQL filter ensures each worker only receives its assigned jobs.

### Throttle Handling

Both Microsoft Graph and Exchange Online enforce rate limits. The worker detects throttling at the runspace level and retries automatically. Functions do not need to handle throttling.

**Detection patterns:**

- **Graph API**: `TooManyRequests`, `429`, `throttled`, `Rate limit`
- **Exchange Online**: `Server Busy`, `ServerBusyException`, `MicroDelay`, `BackoffException`, `Too many concurrent connections`

**Retry strategy:**

- Exponential backoff with jitter: `delay = min(base * 2^attempt, max) + random(0, delay * 0.3)`
- Respects `Retry-After` header when present
- Default: 5 retries, 2-second base delay, 120-second max delay
- Non-throttling exceptions are reported immediately as failures

### Logging and Monitoring

The worker logs to Application Insights using the .NET `TelemetryClient`:

- **Traces**: Structured log messages with severity levels and custom properties
- **Exceptions**: Full exception details with job context
- **Events**: Lifecycle events (startup, shutdown, job dispatched)
- **Metrics**: Job duration, throttle retries, dispatch counts

All telemetry includes `WorkerId` as a standard dimension. All log messages are also written to stdout for container log aggregation via Azure Monitor.

### Health Check

The worker runs an HTTP health check server on port 8080, used by ACA for liveness and readiness probes.

---

## Standard Function Library

The cloud worker ships with 14 standard functions organized across two modules: `EntraFunctions.ps1` (8 functions for Entra ID operations) and `ExchangeFunctions.ps1` (6 functions for Exchange Online operations).

### Entra ID Functions (EntraFunctions.ps1)

#### New-EntraUser

Creates a new user in Entra ID. Generates a random password if none is provided.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `DisplayName` | string | Yes | -- | Display name for the new user |
| `UserPrincipalName` | string | Yes | -- | UPN for the new user |
| `MailNickname` | string | Yes | -- | Mail alias for the new user |
| `Password` | string | No | (random) | Initial password |
| `AccountEnabled` | bool | No | `true` | Whether the account is enabled |
| `ForceChangePasswordNextSignIn` | bool | No | `true` | Force password change on next sign-in |

**Returns:** Object with `Id`, `DisplayName`, `UserPrincipalName`, `MailNickname`

#### Set-EntraUserUPN

Changes a user's User Principal Name.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `UserId` | string | Yes | Object ID or current UPN of the user |
| `NewUserPrincipalName` | string | Yes | New UPN to assign |

**Returns:** Boolean

#### Add-EntraGroupMember

Adds a user to an Entra ID group.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `GroupId` | string | Yes | Object ID of the target group |
| `UserId` | string | Yes | Object ID of the user to add |

**Returns:** Boolean

#### Remove-EntraGroupMember

Removes a user from an Entra ID group.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `GroupId` | string | Yes | Object ID of the target group |
| `UserId` | string | Yes | Object ID of the user to remove |

**Returns:** Boolean

#### New-EntraB2BInvitation

Invites an existing internal user to B2B collaboration using `New-MgInvitation` with `InvitedUser.Id`. The user's `Mail` property must already be set to the external email address they will use for B2B collaboration before calling this function. The user's object ID, UPN, group memberships, and app assignments are retained. After redemption the user authenticates with their external identity provider.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `UserId` | string | Yes | -- | Object ID of the existing internal user |
| `InvitedUserEmailAddress` | string | Yes | -- | External email address (must match user's Mail property) |
| `InviteRedirectUrl` | string | No | `https://myapps.microsoft.com` | URL to redirect after redemption |
| `SendInvitationMessage` | bool | No | `true` | Whether to send the invitation email |
| `CustomizedMessageBody` | string | No | -- | Custom message for the invitation email |
| `InvitedUserType` | string | No | `Guest` | User type after conversion: `Guest` or `Member` |

**Returns:** Object with `Id`, `InvitedUserEmailAddress`, `InvitedUserDisplayName`, `InviteRedeemUrl`, `Status`, `InvitedUserId`, `InvitedUserType`

#### Convert-EntraB2BToInternal

Converts an externally authenticated B2B user to an internal member via the beta Graph API (`POST /beta/users/{id}/convertExternalToInternalMemberUser`). For cloud-managed users this requires a UPN and password profile. Requires `User-ConvertToInternal.ReadWrite.All` or `User.ReadWrite.All` permission. The calling principal must have at least User Administrator role.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `UserId` | string | Yes | -- | Object ID of the B2B user to convert |
| `NewUserPrincipalName` | string | Yes | -- | New UPN for the converted user |
| `Mail` | string | No | -- | Email address to set during conversion |
| `Password` | string | No | (random) | Password for the converted user |
| `ForceChangePasswordNextSignIn` | bool | No | `true` | Force password change on next sign-in |

**Returns:** Object with `UserId`, `PreviousUPN`, `NewUserPrincipalName`, `DisplayName`, `Mail`, `PreviousUserType`, `ConvertedToInternalUserDateTime`, `GeneratedPassword`

#### Test-EntraAttributeMatch

Checks if a user attribute matches an expected value. Supports multi-value collections.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `UserId` | string | Yes | -- | Object ID or UPN of the user |
| `AttributeName` | string | Yes | -- | Name of the attribute to check |
| `ExpectedValue` | string | Yes | -- | Expected value to match against |
| `IsMultiValue` | bool | No | `false` | Treat the attribute as a collection and check for membership |

**Returns:** Object with `Match` (bool), `AttributeName`, `CurrentValue`, `ExpectedValue`, `UserId`, `IsMultiValue`

#### Test-EntraGroupMembership

Checks if a user is a member of an Entra ID group. Uses the `checkMemberGroups` API with fallback to member enumeration.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `GroupId` | string | Yes | Object ID of the group |
| `UserId` | string | Yes | Object ID of the user |

**Returns:** Object with `IsMember` (bool), `GroupId`, `UserId`

### Exchange Online Functions (ExchangeFunctions.ps1)

#### Add-ExchangeSecondaryEmail

Adds a secondary (proxy) email address to a mail user. Ensures lowercase `smtp:` prefix for secondary addresses.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the mail user (UPN, alias, or distinguished name) |
| `EmailAddress` | string | Yes | Secondary email address to add |

**Returns:** Boolean

#### Set-ExchangePrimaryEmail

Changes the primary SMTP address on a mail user. Uses uppercase `SMTP:` prefix for the primary address. The previous primary address is demoted to a secondary address.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the mail user |
| `NewPrimaryEmail` | string | Yes | New primary email address |

**Returns:** Boolean

#### Set-ExchangeExternalAddress

Changes the external/target email address on a mail user.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the mail user |
| `ExternalEmailAddress` | string | Yes | New external email address |

**Returns:** Boolean

#### Set-ExchangeMailUserGuids

Assigns an Exchange GUID and optionally an Archive GUID to a mail user. Used during migration to stamp source GUIDs onto the target mail user before mailbox migration.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Identity` | string | Yes | Identity of the mail user |
| `ExchangeGuid` | string | Yes | Exchange GUID to assign |
| `ArchiveGuid` | string | No | Archive GUID to assign (only set if provided) |

**Returns:** Boolean

#### Test-ExchangeAttributeMatch

Checks if a mail user attribute matches an expected value. Supports multi-value collections (e.g., `EmailAddresses`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `Identity` | string | Yes | -- | Identity of the mail user (UPN, alias, etc.) |
| `AttributeName` | string | Yes | -- | Name of the attribute to check |
| `ExpectedValue` | string | Yes | -- | Expected value to match against |
| `IsMultiValue` | bool | No | `false` | Treat the attribute as a collection and check for membership |

**Returns:** Object with `Match` (bool), `AttributeName`, `CurrentValue`, `ExpectedValue`, `Identity`, `IsMultiValue`

#### Test-ExchangeGroupMembership

Checks if a user is a member of an Exchange Online distribution group. Matches against `PrimarySmtpAddress`, `Alias`, `Identity`, and `ExternalDirectoryObjectId`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `GroupIdentity` | string | Yes | Identity of the distribution group (name, alias, or email) |
| `MemberIdentity` | string | Yes | Identity of the user to check (UPN, alias, or email) |

**Returns:** Object with `IsMember` (bool), `GroupIdentity`, `MemberIdentity`

### Runbook YAML Examples for Standard Functions

```yaml
# Create a user
- name: create-user
  worker_id: worker-01
  function: New-EntraUser
  params:
    DisplayName: "{{DisplayName}}"
    UserPrincipalName: "{{TargetUPN}}"
    MailNickname: "{{MailNickname}}"
  output_params:
    TargetUserId: "Id"

# Add to group
- name: add-to-group
  worker_id: worker-01
  function: Add-EntraGroupMember
  params:
    GroupId: "00000000-0000-0000-0000-000000000000"
    UserId: "{{TargetUserId}}"

# Set Exchange GUIDs
- name: stamp-guids
  worker_id: worker-01
  function: Set-ExchangeMailUserGuids
  params:
    Identity: "{{TargetUPN}}"
    ExchangeGuid: "{{MailboxGuid}}"
    ArchiveGuid: "{{ArchiveGuid}}"

# Validate attribute
- name: verify-upn
  worker_id: worker-01
  function: Test-EntraAttributeMatch
  params:
    UserId: "{{TargetUserId}}"
    AttributeName: UserPrincipalName
    ExpectedValue: "{{TargetUPN}}"
  output_params:
    UPNMatch: "Match"
```

---

## Function Contract

All functions -- both standard and custom -- must follow this contract:

1. **Parameters**: Accept named parameters matching the `Parameters` object in the job message. Use `[CmdletBinding()]` and `[Parameter(Mandatory)]` attributes.
2. **Return values**: Use one of the four return patterns described below.
3. **Errors**: Throw exceptions on failure. The worker catches and reports these to the orchestrator.
4. **Sessions**: MgGraph and EXO sessions are pre-authenticated in each runspace. Functions can call Graph and EXO cmdlets directly without connecting.
5. **Property naming**: Return object properties should use **camelCase** (e.g., `mailboxGuid`). PowerShell property access is case-insensitive, so `$result.MailboxGuid` and `$result.mailboxGuid` both work in PowerShell code.
6. **Throttling**: Functions do not need to handle throttling. The worker automatically detects and retries throttled requests with exponential backoff and jitter. A failure is only reported when retries are exhausted.

---

## Return Patterns

### 1. Boolean Success

For simple actions with no data to pass forward. No `output_params` needed in the runbook.

```powershell
function Set-UserAttribute {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$UserId,
        [Parameter(Mandatory)][string]$Value
    )
    Update-MgUser -UserId $UserId -Department $Value -ErrorAction Stop
    return $true
}
```

### 2. Data Object with output_params

For lookups and operations that return data for downstream steps. The orchestrator extracts named fields from the result JSON and stores them in the member's `worker_data_json`, where they become available as `{{VariableName}}` template variables in subsequent steps.

```powershell
function Get-MailboxInfo {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$UserId)
    $mailbox = Get-EXOMailbox -Identity $UserId -ErrorAction Stop
    return [PSCustomObject]@{
        mailboxGuid        = $mailbox.ExchangeGuid.ToString()
        primarySmtpAddress = $mailbox.PrimarySmtpAddress
    }
}
```

Runbook configuration:

```yaml
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
    Identity: "{{TargetIdentity}}"
    ExchangeGuid: "{{MailboxGuid}}"     # populated by get-mailbox
```

The `output_params` mapping is `TemplateVariable: "resultFieldName"`. Field lookup is case-insensitive -- `"mailboxGuid"` matches a PowerShell property named `MailboxGuid` or `mailboxGuid`.

### 3. Validation Check (Non-Exception Failure)

For checks that can "fail" without being an error. The orchestrator treats any non-exception return as success. Use `output_params` to capture the pass/fail status for downstream logic.

```powershell
function Test-MigrationReady {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$UserId,
        [Parameter(Mandatory)][string]$RequiredGroup
    )
    $groups = Get-MgUserMemberOf -UserId $UserId -ErrorAction Stop
    $isMember = $groups.AdditionalProperties.displayName -contains $RequiredGroup
    return [PSCustomObject]@{
        ready  = $isMember
        reason = if ($isMember) { 'All prerequisites met' } else { "Not in '$RequiredGroup'" }
    }
}
```

Runbook configuration:

```yaml
- name: check-ready
  worker_id: worker-01
  function: Test-MigrationReady
  params:
    UserId: "{{UserPrincipalName}}"
    RequiredGroup: "Migration-Eligible"
  output_params:
    MigrationReady: "ready"
    ReadyReason: "reason"
```

### 4. Polling (Long-Running Operations)

For operations that span multiple job execution cycles. The function is called repeatedly by the orchestrator until it returns `complete = $true`.

**Convention:**
- Still in progress: return `@{ complete = $false }`
- Done: return `@{ complete = $true; data = @{ ... } }`

When the polling result has `complete = $true`, the orchestrator uses the `data` sub-object (not the top-level result) for `output_params` extraction.

```powershell
function Start-MailboxMove {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$UserId)
    $moveRequest = Get-MoveRequest -Identity $UserId -ErrorAction SilentlyContinue
    if (-not $moveRequest) {
        # First invocation -- start the operation
        New-MoveRequest -Identity $UserId -Remote -ErrorAction Stop
        return [PSCustomObject]@{ complete = $false }
    }
    if ($moveRequest.Status -eq 'Completed') {
        return [PSCustomObject]@{
            complete = $true
            data     = [PSCustomObject]@{
                status      = 'Completed'
                completedAt = $moveRequest.CompletionTimestamp.ToString('o')
            }
        }
    }
    # Still in progress
    return [PSCustomObject]@{ complete = $false }
}
```

Runbook configuration:

```yaml
- name: start-migration
  worker_id: worker-01
  function: Start-MailboxMove
  params:
    UserId: "{{UserPrincipalName}}"
  poll:
    interval: 5m       # Re-invoke every 5 minutes
    timeout: 8h        # Give up after 8 hours
  output_params:
    MoveStatus: "status"         # Extracted from data.status when complete
    CompletedAt: "completedAt"   # Extracted from data.completedAt when complete
```

---

## Writing Custom Functions

### Module Structure

Place custom modules in `modules/CustomFunctions/`:

```
CustomFunctions/
+-- YourModuleName/
|   +-- YourModuleName.psd1    # Module manifest
|   +-- YourModuleName.psm1    # Module implementation
+-- AnotherModule/
    +-- AnotherModule.psd1
    +-- AnotherModule.psm1
```

Modules are automatically discovered and imported during the boot sequence (phase 6). Functions must be exported in the module manifest (`.psd1`).

### Module Manifest Example

```powershell
# YourModuleName.psd1
@{
    ModuleVersion = '1.0.0'
    RootModule    = 'YourModuleName.psm1'
    FunctionsToExport = @(
        'Get-CustomMailboxInfo'
        'Set-CustomUserAttribute'
    )
    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()
}
```

### Deployment Options

- **Baked into image**: Add modules to `CustomFunctions/` before building the container image. Suitable for stable, tested custom logic.
- **Volume mount**: Mount a volume to `CustomFunctions/` in the ACA configuration. Enables updating custom functions without rebuilding the container image.

### Implementation Checklist

1. Create a subdirectory under `CustomFunctions/` with your module name
2. Create a `.psd1` manifest that exports your functions
3. Create a `.psm1` implementation following the function contract
4. Use `[CmdletBinding()]` and `[Parameter(Mandatory)]` on all functions
5. Return `$true` for simple actions or `[PSCustomObject]` for data results
6. Throw on failure -- do not catch and swallow exceptions
7. Use camelCase for return object property names
8. Do not handle throttling -- the worker handles it automatically
9. Do not call `Connect-MgGraph` or `Connect-ExchangeOnline` -- sessions are pre-authenticated

---

## Job Message Format

### Inbound (worker-jobs topic)

```json
{
    "JobId": "550e8400-e29b-41d4-a716-446655440000",
    "BatchId": 42,
    "WorkerId": "worker-01",
    "FunctionName": "New-EntraUser",
    "Parameters": {
        "DisplayName": "Jane Smith",
        "UserPrincipalName": "jane@contoso.com",
        "MailNickname": "jsmith"
    },
    "CorrelationData": {
        "StepExecutionId": 123,
        "IsInitStep": false,
        "RunbookName": "contoso-migration",
        "RunbookVersion": 1
    }
}
```

**Required fields:**
- `JobId` -- Unique identifier for the job (GUID)
- `FunctionName` -- Name of the function to execute (must exist in StandardFunctions or a loaded custom module)
- `Parameters` -- Object whose properties map to the function's parameters

**Optional fields:**
- `BatchId` -- Groups related jobs for orchestrator tracking
- `WorkerId` -- Included in the message body for reference; the subscription filter uses the `WorkerId` application property on the Service Bus message
- `CorrelationData` -- Orchestrator-provided context passed through to the result message

**Service Bus application properties:**

| Property | Example | Purpose |
|----------|---------|---------|
| `WorkerId` | `worker-01` | Subscription SQL filter matches on this |
| `BatchId` | `batch-001` | Optional, for orchestrator correlation |

### Outbound (worker-results topic)

**Success:**

```json
{
    "JobId": "550e8400-e29b-41d4-a716-446655440000",
    "BatchId": 42,
    "WorkerId": "worker-01",
    "FunctionName": "New-EntraUser",
    "Status": "Success",
    "ResultType": "Object",
    "Result": {
        "Id": "abc-123",
        "DisplayName": "Jane Smith",
        "UserPrincipalName": "jane@contoso.com",
        "MailNickname": "jsmith"
    },
    "Error": null,
    "DurationMs": 1234,
    "Timestamp": "2025-01-15T10:30:00Z",
    "CorrelationData": {
        "StepExecutionId": 123,
        "IsInitStep": false,
        "RunbookName": "contoso-migration",
        "RunbookVersion": 1
    }
}
```

**Failure:**

```json
{
    "JobId": "550e8400-e29b-41d4-a716-446655440000",
    "BatchId": 42,
    "WorkerId": "worker-01",
    "FunctionName": "New-EntraUser",
    "Status": "Failure",
    "ResultType": null,
    "Result": null,
    "Error": {
        "Message": "User already exists",
        "Type": "Microsoft.Graph.Models.ODataErrors.ODataError",
        "IsThrottled": false,
        "Attempts": 1
    },
    "DurationMs": 345,
    "Timestamp": "2025-01-15T10:30:00Z",
    "CorrelationData": { }
}
```

**Status values:**
- `Success` -- Function executed without errors
- `Failure` -- Function threw a non-throttling exception (or throttling retries were exhausted)

**ResultType values:**
- `Boolean` -- Result is `true` (success indicator)
- `Object` -- Result is a PSCustomObject with data for the orchestrator

---

## Configuration (Environment Variables)

| Variable | Description | Default |
|----------|-------------|---------|
| `WORKER_ID` | Unique worker pool identifier (used for Service Bus subscription filtering) | (required) |
| `MAX_PARALLELISM` | Maximum concurrent runspaces in the pool | (required) |
| `SERVICE_BUS_NAMESPACE` | FQDN of the Service Bus namespace | (required) |
| `WORKER_JOBS_TOPIC` | Topic name for inbound jobs | `worker-jobs` |
| `WORKER_RESULTS_TOPIC` | Topic name for outbound results | `worker-results` |
| `KEY_VAULT_NAME` | Key Vault name for certificate retrieval | (required) |
| `TARGET_TENANT_ID` | Entra ID tenant ID of the target M365 tenant | (required) |
| `TARGET_APP_ID` | App registration client ID for target tenant auth | (required) |
| `CERT_SECRET_NAME` | Key Vault secret name for the PFX certificate | (required) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights connection string | (optional) |
| `IDLE_TIMEOUT_SEC` | Seconds of idle before graceful shutdown (0 to disable) | `300` |
| `GRACE_PERIOD_SEC` | Seconds to wait for in-flight jobs before forced shutdown | `30` |

---

## Project File Layout

```
cloud-worker/
+-- Dockerfile, .dockerignore, docker-compose.yml
+-- src/
|   +-- worker.ps1                         # Main entry point (8-phase boot sequence)
|   +-- config.ps1                         # Env var loader + validation
|   +-- auth.ps1                           # Managed identity, Key Vault cert retrieval, MgGraph + EXO cert auth
|   +-- servicebus.ps1                     # .NET SDK for send/receive (Azure.Messaging.ServiceBus)
|   +-- runspace-manager.ps1               # RunspacePool creation, per-runspace auth, async dispatch
|   +-- job-dispatcher.ps1                 # Main loop: receive -> validate -> dispatch -> collect -> send results
|   +-- logging.ps1                        # App Insights TelemetryClient + console fallback
+-- modules/
|   +-- StandardFunctions/
|   |   +-- StandardFunctions.psd1/psm1    # Module manifest + loader
|   |   +-- EntraFunctions.ps1             # 8 functions: user, group, B2B, validation
|   |   +-- ExchangeFunctions.ps1          # 6 functions: mail user, validation
|   +-- CustomFunctions/
|       +-- ExampleCustomModule/           # Sample custom function module
+-- tests/
|   +-- Submit-TestJob.ps1                 # CSV-driven test job submitter
|   +-- sample-jobs.csv
|   +-- Test-WorkerLocal.ps1               # Parse + structure validation (20 tests)
```

---

## PowerShell Gotchas

These are real bugs encountered during development of this worker:

- **`$Error` is read-only**: The `$Error` automatic variable is read-only in PowerShell. The codebase uses `$ErrorInfo` as the parameter name in `New-JobResult` and callers pass `-ErrorInfo`.
- **String interpolation with colon**: `"Runspace $Index:"` causes parse errors due to `:` after the variable name. Use `"Runspace ${Index}:"` syntax instead.
- **EXO ShowBanner in scriptblocks**: `Connect-ExchangeOnline -ShowBanner:$false` causes parse issues in scriptblocks. Use splatting instead.
- **`New-RandomPassword` is internal**: The utility function `New-RandomPassword` lives in `EntraFunctions.ps1` and is not exported. It is used internally by `New-EntraUser` and `Convert-EntraB2BToInternal`.
