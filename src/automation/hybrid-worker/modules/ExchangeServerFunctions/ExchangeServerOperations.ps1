<#
.SYNOPSIS
    Exchange Server Management Shell functions for the Hybrid Worker.
.DESCRIPTION
    Provides remote mailbox management functions.
    These run in PS 5.1 PSSessions with Exchange Server cmdlets imported.
#>

function New-ExchangeRemoteMailbox {
    <#
    .SYNOPSIS
        Provisions a new remote mailbox in Exchange Server.
    .DESCRIPTION
        Creates a new mail-enabled AD user with a remote routing address pointing
        to Exchange Online. Uses New-RemoteMailbox which creates the AD account and
        mail-enables it in a single operation.
    .PARAMETER Name
        The Name (cn) for the new mailbox.
    .PARAMETER UserPrincipalName
        The UPN for the new mailbox user.
    .PARAMETER RemoteRoutingAddress
        The remote routing address (e.g., user@tenant.mail.onmicrosoft.com).
    .PARAMETER Password
        SecureString password for the new account.
    .PARAMETER FirstName
        Optional first name.
    .PARAMETER LastName
        Optional last name.
    .PARAMETER DisplayName
        Optional display name.
    .PARAMETER Alias
        Optional mail alias.
    .PARAMETER OrganizationalUnit
        Optional OU distinguished name.
    .PARAMETER SamAccountName
        Optional SAM account name.
    .EXAMPLE
        $job = @{
            "FunctionName" = "New-ExchangeRemoteMailbox"
            "Parameters" = @{
                "Name" = "John Doe"
                "UserPrincipalName" = "jdoe@contoso.com"
                "RemoteRoutingAddress" = "jdoe@contoso.mail.onmicrosoft.com"
                "Password" = "(SecureString)"
            }
        }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$UserPrincipalName,
        [Parameter(Mandatory)] [string]$RemoteRoutingAddress,
        [Parameter(Mandatory)] [SecureString]$Password,
        [string]$FirstName,
        [string]$LastName,
        [string]$DisplayName,
        [string]$Alias,
        [string]$OrganizationalUnit,
        [string]$SamAccountName
    )

    $params = @{
        Name                 = $Name
        UserPrincipalName    = $UserPrincipalName
        RemoteRoutingAddress = $RemoteRoutingAddress
        Password             = $Password
        ResetPasswordOnNextLogon = $false
        ErrorAction          = 'Stop'
    }
    if ($FirstName) { $params['FirstName'] = $FirstName }
    if ($LastName) { $params['LastName'] = $LastName }
    if ($DisplayName) { $params['DisplayName'] = $DisplayName }
    if ($Alias) { $params['Alias'] = $Alias }
    if ($OrganizationalUnit) { $params['OnPremisesOrganizationalUnit'] = $OrganizationalUnit }
    if ($SamAccountName) { $params['SamAccountName'] = $SamAccountName }

    $mailbox = New-RemoteMailbox @params
    $result = Get-RemoteMailbox -Identity $mailbox.Identity -ErrorAction Stop

    return [PSCustomObject]@{
        Identity             = $result.Identity
        Guid                 = $result.Guid.ToString()
        RemoteRoutingAddress = $result.RemoteRoutingAddress.ToString()
        PrimarySmtpAddress   = $result.PrimarySmtpAddress.ToString()
        SamAccountName       = $result.SamAccountName
    }
}
