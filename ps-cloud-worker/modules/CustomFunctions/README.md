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
2. **Return values**:
   - Return `$true` for simple success (boolean result type)
   - Return a `PSCustomObject` to pass data back to the orchestrator (object result type)
3. **Errors**: Throw exceptions on failure. The worker catches and reports these to the orchestrator.

Throttling exceptions from Microsoft Graph or Exchange Online are automatically detected and retried by the worker. Non-throttling exceptions are reported as job failures.

## Example Job Message

```json
{
    "JobId": "550e8400-e29b-41d4-a716-446655440000",
    "BatchId": "batch-001",
    "WorkerId": "worker-01",
    "FunctionName": "Set-CustomUserAttribute",
    "Parameters": {
        "UserId": "user@contoso.com",
        "AttributeName": "jobTitle",
        "AttributeValue": "Senior Engineer"
    }
}
```

See `ExampleCustomModule/` for a complete example.
