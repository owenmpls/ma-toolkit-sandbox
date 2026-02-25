@{
    RootModule        = 'SampleCustomFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'e5f6a7b8-c9d0-1234-ef01-567890123456'
    Author            = 'Migration Automation Toolkit'
    Description       = 'Sample custom functions demonstrating extensibility patterns for each service type.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'Set-SampleADAttribute',
        'Test-SampleExchangeMailbox',
        'Get-SampleSPOSiteInfo',
        'Get-SampleTeamInfo'
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()

    PrivateData = @{
        PSData = @{}
        RequiredServices = @('activeDirectory', 'exchangeServer', 'sharepointOnline', 'teams')
        ExecutionEngine  = 'SessionPool'
    }
}
