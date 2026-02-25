<#
.SYNOPSIS
    Active Directory migration operations for the Hybrid Worker.
.DESCRIPTION
    Provides user creation and attribute management functions with multi-forest support.
    All functions use Get-ADForestConnection to obtain validated server/credential pairs.
#>

function New-ADMigrationUser {
    <#
    .SYNOPSIS
        Creates a new AD user in the specified forest for migration.
    .DESCRIPTION
        Creates a new Active Directory user account with the specified properties.
        Uses Get-ADForestConnection for multi-forest support.
    .PARAMETER TargetForest
        The forest name where the user should be created (e.g., 'corp.contoso.com').
    .PARAMETER SamAccountName
        The SAM account name for the new user.
    .PARAMETER UserPrincipalName
        The UPN for the new user.
    .PARAMETER DisplayName
        The display name for the new user.
    .PARAMETER OrganizationalUnit
        The distinguished name of the OU where the user should be created.
    .PARAMETER GivenName
        Optional first name.
    .PARAMETER Surname
        Optional last name.
    .PARAMETER Description
        Optional description.
    .EXAMPLE
        New-ADMigrationUser -TargetForest 'corp.contoso.com' -SamAccountName 'jdoe' `
            -UserPrincipalName 'jdoe@corp.contoso.com' -DisplayName 'John Doe' `
            -OrganizationalUnit 'OU=MigrationUsers,DC=corp,DC=contoso,DC=com'
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$TargetForest,
        [Parameter(Mandatory)] [string]$SamAccountName,
        [Parameter(Mandatory)] [string]$UserPrincipalName,
        [Parameter(Mandatory)] [string]$DisplayName,
        [Parameter(Mandatory)] [string]$OrganizationalUnit,
        [string]$GivenName,
        [string]$Surname,
        [string]$Description
    )

    $conn = Get-ADForestConnection -ForestName $TargetForest

    $params = @{
        SamAccountName    = $SamAccountName
        UserPrincipalName = $UserPrincipalName
        Name              = $DisplayName
        DisplayName       = $DisplayName
        Path              = $OrganizationalUnit
        Enabled           = $true
        Server            = $conn.Server
        Credential        = $conn.Credential
        PassThru          = $true
        ErrorAction       = 'Stop'
    }
    if ($GivenName) { $params['GivenName'] = $GivenName }
    if ($Surname) { $params['Surname'] = $Surname }
    if ($Description) { $params['Description'] = $Description }

    $user = New-ADUser @params

    return [PSCustomObject]@{
        DistinguishedName = $user.DistinguishedName
        SamAccountName    = $user.SamAccountName
        UserPrincipalName = $user.UserPrincipalName
        ObjectGUID        = $user.ObjectGUID.ToString()
        Enabled           = $user.Enabled
    }
}

function Set-ADUserAttribute {
    <#
    .SYNOPSIS
        Sets a single attribute on an AD user in the specified forest.
    .DESCRIPTION
        Uses Set-ADUser -Replace to set a single attribute value on the target user.
    .PARAMETER TargetForest
        The forest name where the user exists.
    .PARAMETER Identity
        The user identity (DN, GUID, SamAccountName, or SID).
    .PARAMETER AttributeName
        The AD attribute to set.
    .PARAMETER AttributeValue
        The value to set on the attribute.
    .EXAMPLE
        Set-ADUserAttribute -TargetForest 'corp.contoso.com' -Identity 'jdoe' `
            -AttributeName 'extensionAttribute1' -AttributeValue 'MigrationBatch42'
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$TargetForest,
        [Parameter(Mandatory)] [string]$Identity,
        [Parameter(Mandatory)] [string]$AttributeName,
        [Parameter(Mandatory)] $AttributeValue
    )

    $conn = Get-ADForestConnection -ForestName $TargetForest

    Set-ADUser -Identity $Identity `
        -Replace @{ $AttributeName = $AttributeValue } `
        -Server $conn.Server `
        -Credential $conn.Credential `
        -ErrorAction Stop

    return $true
}
