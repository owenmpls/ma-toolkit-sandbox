<#
.SYNOPSIS
    Example custom function module for the PowerShell Cloud Worker.
.DESCRIPTION
    Demonstrates all supported return patterns for custom migration functions.
    Custom modules are automatically loaded from the CustomFunctions directory
    at worker startup.

    Custom functions follow the same contract as standard functions:
    - Accept parameters matching the job message Parameters object
    - Return $true (boolean success) or a PSCustomObject (data result)
    - Throw on failure (the worker handles exception routing)

    Each function below includes a "Runbook YAML" doc section showing how to
    configure the corresponding step in a runbook definition.
#>

function Set-ExampleUserAttribute {
    <#
    .SYNOPSIS
        Example: Sets a custom extension attribute on an Entra ID user.
    .DESCRIPTION
        Demonstrates a simple action that returns $true on success.
        Use this pattern when the step has no data to pass forward.

        ## Runbook YAML

        ```yaml
        steps:
          - name: set-attribute
            worker_id: worker-01
            function: Set-ExampleUserAttribute
            params:
              UserId: "{{UserPrincipalName}}"
              AttributeName: "extension_abc123_migrationStatus"
              AttributeValue: "InProgress"
        ```
    .PARAMETER UserId
        The object ID or UPN of the user.
    .PARAMETER AttributeName
        The extension attribute name (e.g., 'extension_<appid>_customField').
    .PARAMETER AttributeValue
        The value to set.
    .OUTPUTS
        Boolean. Returns $true on success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$AttributeName,

        [Parameter(Mandatory)]
        [string]$AttributeValue
    )

    $params = @{
        $AttributeName = $AttributeValue
    }

    Update-MgUser -UserId $UserId @params -ErrorAction Stop

    return $true
}

function Get-ExampleMailboxInfo {
    <#
    .SYNOPSIS
        Example: Retrieves mailbox information for a user.
    .DESCRIPTION
        Demonstrates returning a multi-field PSCustomObject so values can be
        captured via output_params and used in subsequent steps.

        ## Runbook YAML

        ```yaml
        steps:
          - name: get-mailbox
            worker_id: worker-01
            function: Get-ExampleMailboxInfo
            params:
              UserId: "{{UserPrincipalName}}"
            output_params:
              MailboxGuid: "mailboxGuid"
              PrimarySmtpAddress: "primarySmtpAddress"

          - name: use-mailbox-guid
            worker_id: worker-01
            function: Set-ExchangeMailUserGuids
            params:
              Identity: "{{TargetIdentity}}"
              ExchangeGuid: "{{MailboxGuid}}"
        ```

        In this example, `Get-ExampleMailboxInfo` returns an object with
        `mailboxGuid` and `primarySmtpAddress`. The orchestrator extracts
        these into the member's worker_data_json, making `{{MailboxGuid}}`
        and `{{PrimarySmtpAddress}}` available in later step templates.
    .PARAMETER UserId
        The object ID or UPN of the user.
    .OUTPUTS
        PSCustomObject with mailboxGuid and primarySmtpAddress fields.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId
    )

    $mailbox = Get-EXOMailbox -Identity $UserId -ErrorAction Stop

    return [PSCustomObject]@{
        mailboxGuid        = $mailbox.ExchangeGuid.ToString()
        primarySmtpAddress = $mailbox.PrimarySmtpAddress
        mailboxType        = $mailbox.RecipientTypeDetails
    }
}

function Test-ExampleMigrationReady {
    <#
    .SYNOPSIS
        Example: Validates that a user meets migration prerequisites.
    .DESCRIPTION
        Demonstrates a validation check that returns a pass/fail result
        with details. The orchestrator treats any non-exception return as
        success, so validation failures should be reported in the return
        object (not thrown as exceptions) unless you want the step to fail.

        ## Runbook YAML

        ```yaml
        steps:
          - name: check-ready
            worker_id: worker-01
            function: Test-ExampleMigrationReady
            params:
              UserId: "{{UserPrincipalName}}"
              RequiredGroup: "Migration-Eligible"
            output_params:
              MigrationReady: "ready"
              ReadyReason: "reason"
        ```
    .PARAMETER UserId
        The object ID or UPN of the user.
    .PARAMETER RequiredGroup
        The display name of the group the user must be a member of.
    .OUTPUTS
        PSCustomObject with ready (boolean) and reason (string) fields.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$RequiredGroup
    )

    # Check group membership
    $groups = Get-MgUserMemberOf -UserId $UserId -ErrorAction Stop
    $isMember = $groups.AdditionalProperties.displayName -contains $RequiredGroup

    if (-not $isMember) {
        return [PSCustomObject]@{
            ready  = $false
            reason = "User is not a member of '$RequiredGroup'"
        }
    }

    # Check license assignment
    $licenses = Get-MgUserLicenseDetail -UserId $UserId -ErrorAction Stop
    if ($licenses.Count -eq 0) {
        return [PSCustomObject]@{
            ready  = $false
            reason = 'User has no license assignments'
        }
    }

    return [PSCustomObject]@{
        ready  = $true
        reason = 'All prerequisites met'
    }
}

function Start-ExampleLongOperation {
    <#
    .SYNOPSIS
        Example: Starts or polls a long-running migration operation.
    .DESCRIPTION
        Demonstrates the polling convention for operations that take longer
        than a single job execution cycle. The function is called repeatedly
        by the orchestrator until it returns { complete = $true }.

        Polling convention:
        - Still in progress: return @{ complete = $false }
        - Done: return @{ complete = $true; data = @{ ... } }

        The orchestrator uses the `poll` config to control re-invocation
        interval and timeout. When complete, values from `data` are
        available to output_params.

        ## Runbook YAML

        ```yaml
        steps:
          - name: start-migration
            worker_id: worker-01
            function: Start-ExampleLongOperation
            params:
              UserId: "{{UserPrincipalName}}"
              OperationType: "MailboxMove"
            poll:
              interval: 2m
              timeout: 4h
            output_params:
              MoveStatus: "status"
        ```
    .PARAMETER UserId
        The object ID or UPN of the user.
    .PARAMETER OperationType
        The type of long-running operation to start or check.
    .OUTPUTS
        PSCustomObject following the polling convention:
        { complete = $false } while in progress,
        { complete = $true; data = @{ status = "..."; completedAt = "..." } } when done.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$OperationType
    )

    # In a real implementation, you'd check the status of a previously-started
    # operation (e.g., a mailbox move request) and return the polling result.
    #
    # First call: start the operation, return incomplete
    # Subsequent calls: check status, return complete when done

    $moveRequest = Get-MoveRequest -Identity $UserId -ErrorAction SilentlyContinue

    if (-not $moveRequest) {
        # First invocation — start the operation
        New-MoveRequest -Identity $UserId -Remote -ErrorAction Stop
        return [PSCustomObject]@{
            complete = $false
        }
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
    return [PSCustomObject]@{
        complete = $false
    }
}
