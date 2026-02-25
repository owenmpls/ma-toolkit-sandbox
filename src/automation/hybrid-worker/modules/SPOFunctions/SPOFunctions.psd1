@{
    RootModule        = 'SPOFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'c3d4e5f6-a7b8-9012-cdef-345678901234'
    Author            = 'Migration Automation Toolkit'
    Description       = 'SharePoint Online functions for the Hybrid Worker.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'New-MigrationSPOSite'
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()

    PrivateData = @{
        PSData = @{}
        RequiredService = 'sharepointOnline'
        ExecutionEngine = 'SessionPool'
    }
}
