<#
.SYNOPSIS
    Exchange Server Management Shell functions for the Hybrid Worker.
.DESCRIPTION
    Provides remote mailbox management and validation functions.
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

function Set-ExchangeRemoteMailboxAttributes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Identity,
        [hashtable]$Attributes = @{}
    )

    if ($Attributes.Count -gt 0) {
        Set-RemoteMailbox -Identity $Identity @Attributes -ErrorAction Stop
    }
    return $true
}

function Test-ExchangeRemoteMailboxMatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Identity,
        [Parameter(Mandatory)] [string]$AttributeName,
        [Parameter(Mandatory)] $ExpectedValue
    )

    $mailbox = Get-RemoteMailbox -Identity $Identity -ErrorAction Stop
    $actualValue = $mailbox.$AttributeName

    # Handle email address collections
    if ($actualValue -is [System.Collections.IEnumerable] -and $actualValue -isnot [string]) {
        $actualValue = @($actualValue | ForEach-Object { $_.ToString() })
        $isMatch = $actualValue -contains $ExpectedValue
    }
    else {
        $actualValue = if ($null -ne $actualValue) { $actualValue.ToString() } else { $null }
        $isMatch = ($actualValue -eq $ExpectedValue)
    }

    return [PSCustomObject]@{
        match    = $isMatch
        expected = $ExpectedValue
        actual   = $actualValue
    }
}
