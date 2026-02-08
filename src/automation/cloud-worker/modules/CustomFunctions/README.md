# Custom Functions

Place custom function modules in this directory. Each module should be in its own subdirectory with a `.psd1` manifest and `.psm1` module file.

## Structure

```
CustomFunctions/
├── YourModuleName/
│   ├── YourModuleName.psd1    # Module manifest
│   └── YourModuleName.psm1    # Module implementation
└── AnotherModule/
    ├── AnotherModule.psd1
    └── AnotherModule.psm1
```

## Function Contract

Custom functions must follow this contract:

1. **Parameters**: Accept named parameters matching the `Parameters` object in the job message
2. **Return values**: See [Return Types](#return-types) below
3. **Errors**: Throw exceptions on failure. The worker catches and reports these to the orchestrator.

## Return Types

| Return Value | ResultType | When to Use | Example |
|---|---|---|---|
| `$true` | Boolean | Simple action with no data to pass forward | `Set-ExampleUserAttribute` |
| `PSCustomObject` | Object | Lookup/validation returning data for `output_params` | `Get-ExampleMailboxInfo` |
| `@{ complete = $false }` | Object | Long-running operation, still in progress (polling) | `Start-ExampleLongOperation` |
| `@{ complete = $true; data = @{...} }` | Object | Long-running operation, finished (polling) | `Start-ExampleLongOperation` |

**Important:** Property names in returned objects should use **camelCase** (e.g., `mailboxGuid`, not `MailboxGuid`). This matches the JSON serialization expected by the orchestrator. PowerShell property access is case-insensitive, so `$result.MailboxGuid` and `$result.mailboxGuid` both work in PS code.

## Output Params

Return values from a step can be captured and made available to subsequent steps via `output_params` in the runbook YAML. The orchestrator extracts named fields from the result JSON and stores them in the member's `worker_data_json`, where they become available as `{{VariableName}}` template variables.

### Runbook example: two-step data flow

```yaml
phases:
  - name: prepare
    steps:
      - name: get-mailbox
        worker_id: worker-01
        function: Get-ExampleMailboxInfo
        params:
          UserId: "{{UserPrincipalName}}"
        output_params:
          MailboxGuid: "mailboxGuid"          # result.mailboxGuid → {{MailboxGuid}}
          PrimarySmtp: "primarySmtpAddress"   # result.primarySmtpAddress → {{PrimarySmtp}}

      - name: set-guids
        worker_id: worker-01
        function: Set-ExchangeMailUserGuids
        params:
          Identity: "{{TargetIdentity}}"
          ExchangeGuid: "{{MailboxGuid}}"     # ← populated by get-mailbox
```

The `output_params` mapping is `TemplateVariable: "resultFieldName"`. Field lookup is **case-insensitive** — `"mailboxGuid"` matches a PS property named `MailboxGuid` or `mailboxGuid`.

## Polling Convention

For long-running operations that span multiple job cycles, use the polling pattern:

1. **First invocation**: Start the operation, return `@{ complete = $false }`
2. **Subsequent invocations**: Check status, return `@{ complete = $false }` while in progress
3. **Final invocation**: Return `@{ complete = $true; data = @{ ... } }` when done

The orchestrator re-invokes the function at the configured `poll.interval` until either `complete` is `$true` or `poll.timeout` is exceeded.

```yaml
steps:
  - name: start-migration
    worker_id: worker-01
    function: Start-ExampleLongOperation
    params:
      UserId: "{{UserPrincipalName}}"
    poll:
      interval: 2m        # Re-invoke every 2 minutes
      timeout: 4h         # Give up after 4 hours
    output_params:
      MoveStatus: "status" # Extracted from data.status when complete
```

When the polling result has `complete = $true`, the orchestrator uses the `data` sub-object (not the top-level result) for `output_params` extraction.

## Error Handling

- **Throw on failure**: When an operation fails, throw an exception. The worker catches it and reports a failure result to the orchestrator.
- **Throttle auto-retry**: Throttling exceptions from Microsoft Graph or Exchange Online (`429` / `Retry-After`) are automatically detected and retried by the worker with exponential backoff. Your function does not need to handle throttling.
- **Validation results**: If your function performs a check that can "fail" without being an error (e.g., a prerequisite check), return the result as data rather than throwing. The orchestrator treats any non-exception return as success — use `output_params` to capture the pass/fail status for downstream logic.

## Example Job Message

```json
{
    "JobId": "550e8400-e29b-41d4-a716-446655440000",
    "BatchId": 42,
    "WorkerId": "worker-01",
    "FunctionName": "Get-ExampleMailboxInfo",
    "Parameters": {
        "UserId": "user@contoso.com"
    }
}
```

See `ExampleCustomModule/` for complete examples of all four return patterns.
