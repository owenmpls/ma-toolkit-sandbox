<#
.SYNOPSIS
    Example custom function module for the PowerShell Cloud Worker.
.DESCRIPTION
    Demonstrates how to create a custom module that extends the worker
    with customer-specific migration logic. Custom modules are automatically
    loaded from the CustomFunctions directory at worker startup.

    Custom functions follow the same contract as standard functions:
    - Accept parameters matching the job message Parameters object
    - Return $true (boolean success) or a PSCustomObject (data result)
    - Throw on failure (the worker handles exception routing)
#>

function Set-CustomUserAttribute {
    <#
    .SYNOPSIS
        Example: Sets a custom extension attribute on an Entra ID user.
    .DESCRIPTION
        This is a sample custom function demonstrating how customers can
        implement their own migration logic. In this example, it sets a
        custom attribute on a user via Microsoft Graph.

        Modify or replace this function with your own business logic.
    .PARAMETER UserId
        The object ID or UPN of the user.
    .PARAMETER AttributeName
        The extension attribute name (e.g., 'extension_<appid>_customField').
    .PARAMETER AttributeValue
        The value to set.
    .OUTPUTS
        PSCustomObject with the attribute name and value that was set.
    .EXAMPLE
        Job message Parameters:
        {
            "UserId": "user@contoso.com",
            "AttributeName": "jobTitle",
            "AttributeValue": "Senior Engineer"
        }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$AttributeName,

        [Parameter(Mandatory)]
        [string]$AttributeValue
    )

    $params = @{
        $AttributeName = $AttributeValue
    }

    Update-MgUser -UserId $UserId @params -ErrorAction Stop

    return [PSCustomObject]@{
        UserId         = $UserId
        AttributeName  = $AttributeName
        AttributeValue = $AttributeValue
        UpdatedAt      = (Get-Date).ToUniversalTime().ToString('o')
    }
}
