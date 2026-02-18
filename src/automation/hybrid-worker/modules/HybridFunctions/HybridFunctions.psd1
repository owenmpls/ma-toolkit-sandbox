@{
    RootModule        = 'HybridFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b2c3d4e5-f6a7-8901-bcde-f23456789012'
    Author            = 'Migration Automation Toolkit'
    Description       = 'On-premises function library for the Hybrid Worker. AD, Exchange Server, SPO, and Teams functions.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        # Active Directory
        'New-ADMigrationUser',
        'Set-ADUserAttributes',
        'Test-ADAttributeMatch',
        'Test-ADGroupMembership',
        'Add-ADGroupMember',
        'Remove-ADGroupMember',
        # Exchange Server
        'New-ExchangeRemoteMailbox',
        'Set-ExchangeRemoteMailboxAttributes',
        'Test-ExchangeRemoteMailboxMatch'
        # SharePoint Online (placeholder)
        # Teams (placeholder)
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()

    PrivateData = @{
        PSData = @{}
        ExecutionEngine   = 'SessionPool'
    }
}
