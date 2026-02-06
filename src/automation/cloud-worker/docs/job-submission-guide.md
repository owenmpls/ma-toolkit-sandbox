# Job Submission Guide

## Message Format

### Job Message (Worker-Jobs Topic)

```json
{
    "JobId": "550e8400-e29b-41d4-a716-446655440000",
    "BatchId": "batch-2026-01-29-001",
    "WorkerId": "worker-01",
    "FunctionName": "New-EntraUser",
    "Parameters": {
        "DisplayName": "Alice Johnson",
        "UserPrincipalName": "alice.johnson@contoso.com",
        "MailNickname": "alicejohnson"
    }
}
```

**Required fields:**
- `JobId` — Unique identifier for the job (GUID recommended)
- `FunctionName` — Name of the function to execute (must exist in StandardFunctions or a loaded custom module)
- `Parameters` — Object whose properties map to the function's parameters

**Optional fields:**
- `BatchId` — Groups related jobs for orchestrator tracking
- `WorkerId` — Included in the message body for reference; the subscription filter uses the `WorkerId` application property on the Service Bus message

### Service Bus Application Properties

When sending a job message, set these application properties on the `ServiceBusMessage`:

| Property | Value | Purpose |
|---|---|---|
| `WorkerId` | e.g., `worker-01` | Subscription SQL filter matches on this |
| `BatchId` | e.g., `batch-001` | Optional, for orchestrator correlation |

### Result Message (Worker-Results Topic)

The worker sends results in this format:

```json
{
    "JobId": "550e8400-e29b-41d4-a716-446655440000",
    "BatchId": "batch-2026-01-29-001",
    "WorkerId": "worker-01",
    "FunctionName": "New-EntraUser",
    "Status": "Success",
    "ResultType": "Object",
    "Result": {
        "Id": "a1b2c3d4-...",
        "DisplayName": "Alice Johnson",
        "UserPrincipalName": "alice.johnson@contoso.com",
        "MailNickname": "alicejohnson"
    },
    "Error": null,
    "DurationMs": 1523,
    "Timestamp": "2026-01-29T14:30:00.000Z"
}
```

**Status values:**
- `Success` — Function executed without errors
- `Failure` — Function threw a non-throttling exception

**ResultType values:**
- `Boolean` — Result is `true` (success indicator)
- `Object` — Result is a PSCustomObject with data for the orchestrator

**Error format (on failure):**
```json
{
    "Message": "User already exists",
    "Type": "Microsoft.Graph.PowerShell.Authentication.Helpers.HttpResponseException",
    "IsThrottled": false,
    "Attempts": 1
}
```

Note: Throttling exceptions are retried internally by the worker and are **not** reported as failures unless all retry attempts are exhausted.

## Function Parameter Reference

### Entra User Functions

**New-EntraUser**
```json
{
    "FunctionName": "New-EntraUser",
    "Parameters": {
        "DisplayName": "Alice Johnson",
        "UserPrincipalName": "alice.johnson@contoso.com",
        "MailNickname": "alicejohnson",
        "Password": "optional-initial-password",
        "AccountEnabled": true,
        "ForceChangePasswordNextSignIn": true
    }
}
```
Returns: Object with `Id`, `DisplayName`, `UserPrincipalName`, `MailNickname`

**Set-EntraUserUPN**
```json
{
    "FunctionName": "Set-EntraUserUPN",
    "Parameters": {
        "UserId": "a1b2c3d4-... or current@upn.com",
        "NewUserPrincipalName": "new@upn.com"
    }
}
```
Returns: Boolean

### Entra Group Functions

**Add-EntraGroupMember**
```json
{
    "FunctionName": "Add-EntraGroupMember",
    "Parameters": {
        "GroupId": "group-object-id",
        "UserId": "user-object-id"
    }
}
```
Returns: Boolean

**Remove-EntraGroupMember**
```json
{
    "FunctionName": "Remove-EntraGroupMember",
    "Parameters": {
        "GroupId": "group-object-id",
        "UserId": "user-object-id"
    }
}
```
Returns: Boolean

### Entra B2B Functions

**New-EntraB2BInvitation**
```json
{
    "FunctionName": "New-EntraB2BInvitation",
    "Parameters": {
        "InvitedUserEmailAddress": "external@partner.com",
        "InvitedUserDisplayName": "External User",
        "InviteRedirectUrl": "https://myapps.microsoft.com",
        "SendInvitationMessage": true,
        "InvitedUserType": "Guest"
    }
}
```
Returns: Object with `Id`, `InvitedUserEmailAddress`, `InviteRedeemUrl`, `Status`, `InvitedUserId`

**Convert-EntraB2BToInternal**
```json
{
    "FunctionName": "Convert-EntraB2BToInternal",
    "Parameters": {
        "UserId": "b2b-user-object-id",
        "NewUserPrincipalName": "internalized@contoso.com",
        "NewDisplayName": "Optional New Name"
    }
}
```
Returns: Object with `UserId`, `PreviousUPN`, `NewUserPrincipalName`, `GeneratedPassword`

### Exchange Mail User Functions

**Add-ExchangeSecondaryEmail**
```json
{
    "FunctionName": "Add-ExchangeSecondaryEmail",
    "Parameters": {
        "Identity": "user@contoso.com",
        "EmailAddress": "alias@contoso.com"
    }
}
```
Returns: Boolean

**Set-ExchangePrimaryEmail**
```json
{
    "FunctionName": "Set-ExchangePrimaryEmail",
    "Parameters": {
        "Identity": "user@contoso.com",
        "NewPrimaryEmail": "newprimary@contoso.com"
    }
}
```
Returns: Boolean

**Set-ExchangeExternalAddress**
```json
{
    "FunctionName": "Set-ExchangeExternalAddress",
    "Parameters": {
        "Identity": "user@contoso.com",
        "ExternalEmailAddress": "user@external.com"
    }
}
```
Returns: Boolean

**Set-ExchangeMailUserGuids**
```json
{
    "FunctionName": "Set-ExchangeMailUserGuids",
    "Parameters": {
        "Identity": "user@contoso.com",
        "ExchangeGuid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
        "ArchiveGuid": "optional-archive-guid"
    }
}
```
Returns: Boolean

### Validation Functions

**Test-EntraAttributeMatch**
```json
{
    "FunctionName": "Test-EntraAttributeMatch",
    "Parameters": {
        "UserId": "user-id-or-upn",
        "AttributeName": "Department",
        "ExpectedValue": "Engineering",
        "IsMultiValue": false
    }
}
```
Returns: Object with `Match`, `AttributeName`, `CurrentValue`, `ExpectedValue`

**Test-ExchangeAttributeMatch**
```json
{
    "FunctionName": "Test-ExchangeAttributeMatch",
    "Parameters": {
        "Identity": "user@contoso.com",
        "AttributeName": "EmailAddresses",
        "ExpectedValue": "smtp:alias@contoso.com",
        "IsMultiValue": true
    }
}
```
Returns: Object with `Match`, `AttributeName`, `CurrentValue`, `ExpectedValue`

**Test-EntraGroupMembership**
```json
{
    "FunctionName": "Test-EntraGroupMembership",
    "Parameters": {
        "GroupId": "group-object-id",
        "UserId": "user-object-id"
    }
}
```
Returns: Object with `IsMember`, `GroupId`, `UserId`

**Test-ExchangeGroupMembership**
```json
{
    "FunctionName": "Test-ExchangeGroupMembership",
    "Parameters": {
        "GroupIdentity": "distribution-group-name-or-email",
        "MemberIdentity": "user@contoso.com"
    }
}
```
Returns: Object with `IsMember`, `GroupIdentity`, `MemberIdentity`

## Using the Test Script

The `Submit-TestJob.ps1` script reads a CSV and enqueues one job per row.

### CSV Format

CSV column names must match the function's parameter names:

**For New-EntraUser:**
```csv
DisplayName,UserPrincipalName,MailNickname
"Alice Johnson",alice.johnson@contoso.com,alicejohnson
"Bob Smith",bob.smith@contoso.com,bobsmith
```

**For Add-EntraGroupMember:**
```csv
GroupId,UserId
a1b2c3d4-...,e5f6a7b8-...
a1b2c3d4-...,c9d0e1f2-...
```

**For Set-ExchangeMailUserGuids:**
```csv
Identity,ExchangeGuid,ArchiveGuid
user1@contoso.com,guid-1,archive-guid-1
user2@contoso.com,guid-2,
```

### Running

```powershell
# Basic usage
./tests/Submit-TestJob.ps1 `
  -CsvPath ./tests/sample-jobs.csv `
  -ServiceBusNamespace 'matoolkit-sb.servicebus.windows.net' `
  -WorkerId 'worker-01' `
  -FunctionName 'New-EntraUser'

# With explicit batch ID
./tests/Submit-TestJob.ps1 `
  -CsvPath ./my-users.csv `
  -ServiceBusNamespace 'matoolkit-sb.servicebus.windows.net' `
  -WorkerId 'worker-01' `
  -FunctionName 'New-EntraUser' `
  -BatchId 'migration-wave-1'
```

### Sending Individual Messages Programmatically

```powershell
# Authenticate
Connect-AzAccount

# Load assemblies (adjust path as needed)
Add-Type -Path '/opt/dotnet-libs/Azure.Core.dll'
Add-Type -Path '/opt/dotnet-libs/Azure.Identity.dll'
Add-Type -Path '/opt/dotnet-libs/Azure.Messaging.ServiceBus.dll'

# Create client
$credential = [Azure.Identity.DefaultAzureCredential]::new()
$client = [Azure.Messaging.ServiceBus.ServiceBusClient]::new(
    'matoolkit-sb.servicebus.windows.net', $credential)
$sender = $client.CreateSender('worker-jobs')

# Build message
$job = @{
    JobId        = [Guid]::NewGuid().ToString()
    BatchId      = 'manual-test'
    WorkerId     = 'worker-01'
    FunctionName = 'Test-EntraAttributeMatch'
    Parameters   = @{
        UserId        = 'user@contoso.com'
        AttributeName = 'Department'
        ExpectedValue = 'Engineering'
        IsMultiValue  = $false
    }
} | ConvertTo-Json -Depth 5

$message = [Azure.Messaging.ServiceBus.ServiceBusMessage]::new($job)
$message.ContentType = 'application/json'
$message.ApplicationProperties['WorkerId'] = 'worker-01'

# Send
$sender.SendMessageAsync($message).GetAwaiter().GetResult()

# Cleanup
$sender.DisposeAsync().GetAwaiter().GetResult()
$client.DisposeAsync().GetAwaiter().GetResult()
```
