<#
.SYNOPSIS
    Exchange Online functions using the ExchangeOnlineManagement module.
.DESCRIPTION
    Provides mail user management, attribute validation, and group
    membership check functions for Exchange Online.
#>

# -----------------------------------------------------------------------------
# Mail User Management
# -----------------------------------------------------------------------------

function Add-ExchangeSecondaryEmail {
    <#
    .SYNOPSIS
        Adds a secondary (proxy) email address to a mail user.
    .PARAMETER Identity
        The identity of the mail user (UPN, alias, or distinguished name).
    .PARAMETER EmailAddress
        The secondary email address to add (e.g., smtp:user@domain.com).
    .OUTPUTS
        Boolean indicating success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Identity,

        [Parameter(Mandatory)]
        [string]$EmailAddress
    )

    # Ensure the address has the smtp: prefix (lowercase = secondary)
    if ($EmailAddress -notmatch '^smtp:' -and $EmailAddress -notmatch '^SMTP:') {
        $EmailAddress = "smtp:$EmailAddress"
    }

    # Use lowercase smtp: to indicate secondary address
    if ($EmailAddress -cmatch '^SMTP:') {
        $EmailAddress = "smtp:$($EmailAddress.Substring(5))"
    }

    Set-MailUser -Identity $Identity -EmailAddresses @{Add = $EmailAddress } -ErrorAction Stop

    return $true
}

function Set-ExchangePrimaryEmail {
    <#
    .SYNOPSIS
        Changes the primary email address on a mail user.
    .DESCRIPTION
        Sets the new address as the primary SMTP address (uppercase SMTP: prefix).
        The previous primary address is demoted to a secondary address.
    .PARAMETER Identity
        The identity of the mail user.
    .PARAMETER NewPrimaryEmail
        The new primary email address.
    .OUTPUTS
        Boolean indicating success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Identity,

        [Parameter(Mandatory)]
        [string]$NewPrimaryEmail
    )

    # Strip any existing prefix and add uppercase SMTP: for primary
    $cleanAddress = $NewPrimaryEmail -replace '^(smtp:|SMTP:)', ''
    $primaryAddress = "SMTP:$cleanAddress"

    Set-MailUser -Identity $Identity -EmailAddresses @{Add = $primaryAddress } -ErrorAction Stop

    return $true
}

function Set-ExchangeExternalAddress {
    <#
    .SYNOPSIS
        Changes the external email address (target address) on a mail user.
    .PARAMETER Identity
        The identity of the mail user.
    .PARAMETER ExternalEmailAddress
        The new external email address.
    .OUTPUTS
        Boolean indicating success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Identity,

        [Parameter(Mandatory)]
        [string]$ExternalEmailAddress
    )

    Set-MailUser -Identity $Identity -ExternalEmailAddress $ExternalEmailAddress -ErrorAction Stop

    return $true
}

function Set-ExchangeMailUserGuids {
    <#
    .SYNOPSIS
        Assigns an Exchange GUID and optionally an Archive GUID to a mail user.
    .DESCRIPTION
        Used during migration to stamp the source Exchange GUID and archive GUID
        onto the target mail user before mailbox migration.
    .PARAMETER Identity
        The identity of the mail user.
    .PARAMETER ExchangeGuid
        The Exchange GUID to assign.
    .PARAMETER ArchiveGuid
        Optional archive GUID to assign. Only set if provided.
    .OUTPUTS
        Boolean indicating success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Identity,

        [Parameter(Mandatory)]
        [string]$ExchangeGuid,

        [string]$ArchiveGuid
    )

    Set-MailUser -Identity $Identity -ExchangeGuid $ExchangeGuid -ErrorAction Stop

    if (-not [string]::IsNullOrWhiteSpace($ArchiveGuid)) {
        Set-MailUser -Identity $Identity -ArchiveGuid $ArchiveGuid -ErrorAction Stop
    }

    return $true
}

# -----------------------------------------------------------------------------
# Validation
# -----------------------------------------------------------------------------

function Test-ExchangeAttributeMatch {
    <#
    .SYNOPSIS
        Checks if an Exchange Online mail user attribute matches a provided value.
    .DESCRIPTION
        Supports both single-value and multi-value attribute checking.
        For multi-value attributes (e.g., EmailAddresses), checks if the expected value exists in the collection.
    .PARAMETER Identity
        The identity of the mail user (UPN, alias, etc.).
    .PARAMETER AttributeName
        The name of the attribute to check.
    .PARAMETER ExpectedValue
        The expected value to match against.
    .PARAMETER IsMultiValue
        If true, treats the attribute as a collection and checks for membership.
    .OUTPUTS
        PSCustomObject with Match (bool), CurrentValue, and ExpectedValue.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Identity,

        [Parameter(Mandatory)]
        [string]$AttributeName,

        [Parameter(Mandatory)]
        [string]$ExpectedValue,

        [bool]$IsMultiValue = $false
    )

    $mailUser = Get-MailUser -Identity $Identity -ErrorAction Stop
    $currentValue = $mailUser.$AttributeName

    $isMatch = $false

    if ($IsMultiValue) {
        if ($null -ne $currentValue -and $currentValue -is [System.Collections.IEnumerable] -and $currentValue -isnot [string]) {
            $isMatch = $currentValue -contains $ExpectedValue
        }
        else {
            $isMatch = [string]$currentValue -eq $ExpectedValue
        }
    }
    else {
        $isMatch = [string]$currentValue -eq $ExpectedValue
    }

    return [PSCustomObject]@{
        Match         = $isMatch
        AttributeName = $AttributeName
        CurrentValue  = $currentValue
        ExpectedValue = $ExpectedValue
        Identity      = $Identity
        IsMultiValue  = $IsMultiValue
    }
}

function Test-ExchangeGroupMembership {
    <#
    .SYNOPSIS
        Checks if a user is a member of an Exchange Online distribution group.
    .PARAMETER GroupIdentity
        The identity of the distribution group (name, alias, or email).
    .PARAMETER MemberIdentity
        The identity of the user to check (UPN, alias, or email).
    .OUTPUTS
        PSCustomObject with IsMember (bool) and details.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$GroupIdentity,

        [Parameter(Mandatory)]
        [string]$MemberIdentity
    )

    $isMember = $false

    try {
        $members = Get-DistributionGroupMember -Identity $GroupIdentity -ResultSize Unlimited -ErrorAction Stop
        $isMember = ($members | Where-Object {
            $_.PrimarySmtpAddress -eq $MemberIdentity -or
            $_.Alias -eq $MemberIdentity -or
            $_.Identity -eq $MemberIdentity -or
            $_.ExternalDirectoryObjectId -eq $MemberIdentity
        } | Measure-Object).Count -gt 0
    }
    catch {
        throw "Failed to check Exchange group membership: $($_.Exception.Message)"
    }

    return [PSCustomObject]@{
        IsMember        = $isMember
        GroupIdentity   = $GroupIdentity
        MemberIdentity  = $MemberIdentity
    }
}
