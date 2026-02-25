@{
    RootModule        = 'ExchangeServerFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b2c3d4e5-f6a7-8901-bcde-f23456789012'
    Author            = 'Migration Automation Toolkit'
    Description       = 'Exchange Server functions for the Hybrid Worker. Remote mailbox provisioning and management.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'New-ExchangeRemoteMailbox'
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()

    PrivateData = @{
        PSData = @{}
        RequiredService = 'exchangeServer'
        ExecutionEngine = 'SessionPool'
    }
}
