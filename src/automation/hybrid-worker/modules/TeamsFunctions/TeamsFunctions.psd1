@{
    RootModule        = 'TeamsFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'd4e5f6a7-b8c9-0123-def0-456789012345'
    Author            = 'Migration Automation Toolkit'
    Description       = 'Microsoft Teams functions for the Hybrid Worker.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'New-MigrationTeam'
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()

    PrivateData = @{
        PSData = @{}
        RequiredService = 'teams'
        ExecutionEngine = 'SessionPool'
    }
}
