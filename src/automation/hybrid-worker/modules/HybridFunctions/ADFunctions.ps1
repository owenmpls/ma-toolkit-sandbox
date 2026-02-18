<#
.SYNOPSIS
    Active Directory functions for the Hybrid Worker.
.DESCRIPTION
    Provides AD user management, group membership, and attribute validation
    functions. These run in PS 5.1 PSSessions with the ActiveDirectory module.
#>

function New-ADMigrationUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$SamAccountName,
        [Parameter(Mandatory)] [string]$UserPrincipalName,
        [Parameter(Mandatory)] [string]$DisplayName,
        [Parameter(Mandatory)] [string]$OrganizationalUnit,
        [string]$GivenName,
        [string]$Surname,
        [string]$Description
    )

    # Skeleton — implement based on migration requirements
    $params = @{
        SamAccountName    = $SamAccountName
        UserPrincipalName = $UserPrincipalName
        Name              = $DisplayName
        DisplayName       = $DisplayName
        Path              = $OrganizationalUnit
        Enabled           = $true
        PassThru          = $true
    }
    if ($GivenName) { $params['GivenName'] = $GivenName }
    if ($Surname) { $params['Surname'] = $Surname }
    if ($Description) { $params['Description'] = $Description }

    $user = New-ADUser @params
    return [PSCustomObject]@{
        ObjectGuid        = $user.ObjectGUID.ToString()
        SamAccountName    = $user.SamAccountName
        UserPrincipalName = $user.UserPrincipalName
        DistinguishedName = $user.DistinguishedName
    }
}

function Set-ADUserAttributes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Identity,
        [Parameter(Mandatory)] [hashtable]$Attributes
    )

    Set-ADUser -Identity $Identity -Replace $Attributes -ErrorAction Stop
    return $true
}

function Test-ADAttributeMatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Identity,
        [Parameter(Mandatory)] [string]$AttributeName,
        [Parameter(Mandatory)] $ExpectedValue
    )

    $user = Get-ADUser -Identity $Identity -Properties $AttributeName -ErrorAction Stop
    $actualValue = $user.$AttributeName

    return [PSCustomObject]@{
        match    = ($actualValue -eq $ExpectedValue)
        expected = $ExpectedValue
        actual   = $actualValue
    }
}

function Test-ADGroupMembership {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$UserIdentity,
        [Parameter(Mandatory)] [string]$GroupIdentity
    )

    $members = Get-ADGroupMember -Identity $GroupIdentity -ErrorAction Stop
    $isMember = $members | Where-Object { $_.SamAccountName -eq $UserIdentity }

    return [PSCustomObject]@{
        isMember = [bool]$isMember
        group    = $GroupIdentity
        user     = $UserIdentity
    }
}

function Add-ADGroupMember {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$GroupIdentity,
        [Parameter(Mandatory)] [string]$MemberIdentity
    )

    Add-ADGroupMember -Identity $GroupIdentity -Members $MemberIdentity -ErrorAction Stop
    return $true
}

function Remove-ADGroupMember {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$GroupIdentity,
        [Parameter(Mandatory)] [string]$MemberIdentity
    )

    Remove-ADGroupMember -Identity $GroupIdentity -Members $MemberIdentity -Confirm:$false -ErrorAction Stop
    return $true
}
