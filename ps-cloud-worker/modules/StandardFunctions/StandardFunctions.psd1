@{
    RootModule        = 'StandardFunctions.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author            = 'Migration Automation Toolkit'
    Description       = 'Standard function library for the PowerShell Cloud Worker. Provides Entra ID (Microsoft Graph) and Exchange Online migration functions.'
    PowerShellVersion = '7.4'

    RequiredModules = @(
        'Microsoft.Graph.Authentication',
        'Microsoft.Graph.Users',
        'Microsoft.Graph.Groups',
        'Microsoft.Graph.Identity.SignIns',
        'ExchangeOnlineManagement'
    )

    FunctionsToExport = @(
        # Entra User
        'New-EntraUser',
        'Set-EntraUserUPN',
        # Entra Group
        'Add-EntraGroupMember',
        'Remove-EntraGroupMember',
        # Entra B2B
        'New-EntraB2BInvitation',
        'Convert-EntraB2BToInternal',
        # Exchange Mail User
        'Add-ExchangeSecondaryEmail',
        'Set-ExchangePrimaryEmail',
        'Set-ExchangeExternalAddress',
        'Set-ExchangeMailUserGuids',
        # Validation
        'Test-EntraAttributeMatch',
        'Test-ExchangeAttributeMatch',
        'Test-EntraGroupMembership',
        'Test-ExchangeGroupMembership'
    )

    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()
}
