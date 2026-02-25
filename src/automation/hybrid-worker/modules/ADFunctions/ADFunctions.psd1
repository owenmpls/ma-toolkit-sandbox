@{
    RootModule        = 'ADFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author            = 'Migration Automation Toolkit'
    Description       = 'Active Directory functions for the Hybrid Worker. Multi-forest support with lazy connection validation.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'Get-ADForestConnection',
        'Reset-ADForestConnection',
        'New-ADMigrationUser',
        'Set-ADUserAttribute'
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()

    PrivateData = @{
        PSData = @{}
        RequiredService = 'activeDirectory'
        ExecutionEngine = 'SessionPool'
    }
}
