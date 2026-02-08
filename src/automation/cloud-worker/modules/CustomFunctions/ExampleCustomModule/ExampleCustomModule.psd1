@{
    RootModule        = 'ExampleCustomModule.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b2c3d4e5-f6a7-8901-bcde-f12345678901'
    Author            = 'Customer Name'
    Description       = 'Example custom function module demonstrating how to extend the PowerShell Cloud Worker with customer-specific logic.'
    PowerShellVersion = '7.4'

    FunctionsToExport = @(
        'Set-ExampleUserAttribute'
        'Get-ExampleMailboxInfo'
        'Test-ExampleMigrationReady'
        'Start-ExampleLongOperation'
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()
}
