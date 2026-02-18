<#
.SYNOPSIS
    Entra ID functions using Microsoft Graph.
.DESCRIPTION
    Provides user management, group membership, B2B collaboration,
    and attribute validation functions for Entra ID.
#>

# -----------------------------------------------------------------------------
# User Management
# -----------------------------------------------------------------------------

function New-EntraUser {
    <#
    .SYNOPSIS
        Creates a new user in Entra ID.
    .PARAMETER DisplayName
        The display name for the new user.
    .PARAMETER UserPrincipalName
        The UPN for the new user.
    .PARAMETER MailNickname
        The mail alias for the new user.
    .PARAMETER Password
        Optional initial password. A random password is generated if not provided.
    .PARAMETER AccountEnabled
        Whether the account is enabled. Defaults to $true.
    .PARAMETER ForceChangePasswordNextSignIn
        Whether to force password change on next sign-in. Defaults to $true.
    .OUTPUTS
        PSCustomObject with Id, DisplayName, UserPrincipalName, and MailNickname of the created user.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DisplayName,

        [Parameter(Mandatory)]
        [string]$UserPrincipalName,

        [Parameter(Mandatory)]
        [string]$MailNickname,

        [string]$Password,

        [bool]$AccountEnabled = $true,

        [bool]$ForceChangePasswordNextSignIn = $true
    )

    if ([string]::IsNullOrWhiteSpace($Password)) {
        $Password = New-RandomPassword
    }

    $passwordProfile = @{
        Password                      = $Password
        ForceChangePasswordNextSignIn = $ForceChangePasswordNextSignIn
    }

    $params = @{
        DisplayName       = $DisplayName
        UserPrincipalName = $UserPrincipalName
        MailNickname      = $MailNickname
        AccountEnabled    = $AccountEnabled
        PasswordProfile   = $passwordProfile
    }

    $user = New-MgUser @params -ErrorAction Stop

    return [PSCustomObject]@{
        Id                = $user.Id
        DisplayName       = $user.DisplayName
        UserPrincipalName = $user.UserPrincipalName
        MailNickname      = $user.MailNickname
    }
}

function Set-EntraUserUPN {
    <#
    .SYNOPSIS
        Changes the User Principal Name for an existing Entra ID user.
    .PARAMETER UserId
        The object ID or current UPN of the user.
    .PARAMETER NewUserPrincipalName
        The new UPN to assign.
    .OUTPUTS
        Boolean indicating success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$NewUserPrincipalName
    )

    Update-MgUser -UserId $UserId -UserPrincipalName $NewUserPrincipalName -ErrorAction Stop

    return $true
}

# -----------------------------------------------------------------------------
# Group Membership
# -----------------------------------------------------------------------------

function Add-EntraGroupMember {
    <#
    .SYNOPSIS
        Adds a user to an Entra ID group.
    .PARAMETER GroupId
        The object ID of the target group.
    .PARAMETER UserId
        The object ID of the user to add.
    .OUTPUTS
        Boolean indicating success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$GroupId,

        [Parameter(Mandatory)]
        [string]$UserId
    )

    $params = @{
        '@odata.id' = "https://graph.microsoft.com/v1.0/directoryObjects/$UserId"
    }

    New-MgGroupMemberByRef -GroupId $GroupId -BodyParameter $params -ErrorAction Stop

    return $true
}

function Remove-EntraGroupMember {
    <#
    .SYNOPSIS
        Removes a user from an Entra ID group.
    .PARAMETER GroupId
        The object ID of the target group.
    .PARAMETER UserId
        The object ID of the user to remove.
    .OUTPUTS
        Boolean indicating success.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$GroupId,

        [Parameter(Mandatory)]
        [string]$UserId
    )

    Remove-MgGroupMemberByRef -GroupId $GroupId -DirectoryObjectId $UserId -ErrorAction Stop

    return $true
}

# -----------------------------------------------------------------------------
# B2B Collaboration
# -----------------------------------------------------------------------------

function New-EntraB2BInvitation {
    <#
    .SYNOPSIS
        Invites an existing internal user to B2B collaboration.
    .DESCRIPTION
        Converts an existing internal user into a B2B collaboration user by sending
        an invitation to their external email address. The user's object ID, UPN,
        group memberships, and app assignments are retained. After redemption the
        user authenticates with their external identity provider.

        The user's Mail property must already be set to the external email address
        they will use for B2B collaboration before calling this function.

        Uses the POST /invitations Graph API with the invitedUser.id field to
        target the existing user object.
    .PARAMETER UserId
        The object ID of the existing internal user to invite for B2B collaboration.
    .PARAMETER InvitedUserEmailAddress
        The external email address for the user. Must match the user's Mail property.
    .PARAMETER InviteRedirectUrl
        The URL to redirect the user after redemption. Defaults to https://myapps.microsoft.com.
    .PARAMETER SendInvitationMessage
        Whether to send the invitation email. Defaults to $true.
    .PARAMETER CustomizedMessageBody
        Optional custom message to include in the invitation email.
    .PARAMETER InvitedUserType
        The user type after conversion: 'Guest' or 'Member'. Defaults to 'Guest'.
        Set to 'Guest' to activate MAU billing instead of per-user licensing.
    .OUTPUTS
        PSCustomObject with invitation details including the invited user's ID and redeem URL.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$InvitedUserEmailAddress,

        [string]$InviteRedirectUrl = 'https://myapps.microsoft.com',

        [bool]$SendInvitationMessage = $true,

        [string]$CustomizedMessageBody,

        [ValidateSet('Guest', 'Member')]
        [string]$InvitedUserType = 'Guest'
    )

    $params = @{
        InvitedUserEmailAddress = $InvitedUserEmailAddress
        InviteRedirectUrl       = $InviteRedirectUrl
        SendInvitationMessage   = $SendInvitationMessage
        InvitedUserType         = $InvitedUserType
        InvitedUser             = @{
            Id = $UserId
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($CustomizedMessageBody)) {
        $params['InvitedUserMessageInfo'] = @{
            CustomizedMessageBody = $CustomizedMessageBody
        }
    }

    $invitation = New-MgInvitation -BodyParameter $params -ErrorAction Stop

    return [PSCustomObject]@{
        Id                      = $invitation.Id
        InvitedUserEmailAddress = $invitation.InvitedUserEmailAddress
        InvitedUserDisplayName  = $invitation.InvitedUserDisplayName
        InviteRedeemUrl         = $invitation.InviteRedeemUrl
        Status                  = $invitation.Status
        InvitedUserId           = $invitation.InvitedUser.Id
        InvitedUserType         = $InvitedUserType
    }
}

function Convert-EntraB2BToInternal {
    <#
    .SYNOPSIS
        Converts an externally authenticated B2B user to an internal member.
    .DESCRIPTION
        Uses the beta Graph API convertExternalToInternalMemberUser action to
        properly convert an external user to an internal member. For cloud-managed
        users this requires a UPN and password profile. Optionally updates the
        mail address during conversion.

        Requires the Microsoft.Graph.Beta.Users.Actions module and either
        User-ConvertToInternal.ReadWrite.All or User.ReadWrite.All permission.
        The calling principal must have at least User Administrator role.
    .PARAMETER UserId
        The object ID of the B2B user to convert.
    .PARAMETER NewUserPrincipalName
        The new UPN for the converted user. Required for cloud-managed users.
    .PARAMETER Mail
        Optional email address to set during conversion.
    .PARAMETER Password
        Optional password. If not provided, a random password is generated.
    .PARAMETER ForceChangePasswordNextSignIn
        Whether the user must change password on next sign-in. Defaults to $true.
    .OUTPUTS
        PSCustomObject with conversion details including the generated password.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$NewUserPrincipalName,

        [string]$Mail,

        [string]$Password,

        [bool]$ForceChangePasswordNextSignIn = $true
    )

    # Retrieve current user state before conversion
    $currentUser = Get-MgUser -UserId $UserId -Property 'DisplayName,UserPrincipalName,UserType,Mail' -ErrorAction Stop

    # Generate password if not provided
    $generatedPassword = if ([string]::IsNullOrWhiteSpace($Password)) {
        New-RandomPassword
    }
    else {
        $Password
    }

    # Build request body for the conversion API
    $bodyParams = @{
        userPrincipalName = $NewUserPrincipalName
        passwordProfile   = @{
            password                      = $generatedPassword
            forceChangePasswordNextSignIn = $ForceChangePasswordNextSignIn
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Mail)) {
        $bodyParams['mail'] = $Mail
    }

    # Call the beta API via Invoke-MgGraphRequest
    $apiUrl = 'https://graph.microsoft.com/beta/users/{0}/convertExternalToInternalMemberUser' -f $UserId
    $response = Invoke-MgGraphRequest -Method POST -Uri $apiUrl -Body $bodyParams -ErrorAction Stop

    return [PSCustomObject]@{
        UserId                          = $UserId
        PreviousUPN                     = $currentUser.UserPrincipalName
        NewUserPrincipalName            = $response.userPrincipalName
        DisplayName                     = $response.displayName
        Mail                            = $response.mail
        PreviousUserType                = $currentUser.UserType
        ConvertedToInternalUserDateTime = $response.convertedToInternalUserDateTime
        GeneratedPassword               = $generatedPassword
    }
}

# -----------------------------------------------------------------------------
# Validation
# -----------------------------------------------------------------------------

function Test-EntraAttributeMatch {
    <#
    .SYNOPSIS
        Checks if an Entra ID user attribute matches a provided value.
    .DESCRIPTION
        Supports both single-value and multi-value attribute checking.
        For multi-value attributes, checks if the expected value exists in the collection.
    .PARAMETER UserId
        The object ID or UPN of the user.
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
        [string]$UserId,

        [Parameter(Mandatory)]
        [string]$AttributeName,

        [Parameter(Mandatory)]
        [string]$ExpectedValue,

        [bool]$IsMultiValue = $false
    )

    $user = Get-MgUser -UserId $UserId -Property $AttributeName -ErrorAction Stop
    $currentValue = $user.$AttributeName

    $isMatch = $false

    if ($IsMultiValue) {
        if ($null -ne $currentValue -and $currentValue -is [System.Collections.IEnumerable] -and $currentValue -isnot [string]) {
            $isMatch = $currentValue -contains $ExpectedValue
        }
        else {
            # If the attribute isn't actually multi-value, do a direct comparison
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
        UserId        = $UserId
        IsMultiValue  = $IsMultiValue
    }
}

function Test-EntraGroupMembership {
    <#
    .SYNOPSIS
        Checks if a user is a member of an Entra ID group.
    .PARAMETER GroupId
        The object ID of the group.
    .PARAMETER UserId
        The object ID of the user.
    .OUTPUTS
        PSCustomObject with IsMember (bool) and details.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$GroupId,

        [Parameter(Mandatory)]
        [string]$UserId
    )

    $isMember = $false

    try {
        # Check membership using the checkMemberGroups API
        $params = @{
            GroupIds = @($GroupId)
        }
        $result = Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/users/$UserId/checkMemberGroups" -Body $params -ErrorAction Stop

        $isMember = $result.value -contains $GroupId
    }
    catch {
        # Fallback: enumerate members and check
        try {
            $members = Get-MgGroupMember -GroupId $GroupId -All -ErrorAction Stop
            $isMember = ($members.Id -contains $UserId)
        }
        catch {
            throw "Failed to check group membership: $($_.Exception.Message)"
        }
    }

    return [PSCustomObject]@{
        IsMember = $isMember
        GroupId  = $GroupId
        UserId   = $UserId
    }
}

# -----------------------------------------------------------------------------
# Utilities (internal)
# -----------------------------------------------------------------------------

function New-RandomPassword {
    <#
    .SYNOPSIS
        Generates a random password meeting Entra ID complexity requirements.
    #>
    [CmdletBinding()]
    param(
        [int]$Length = 16
    )

    $upper = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
    $lower = 'abcdefghijklmnopqrstuvwxyz'
    $digits = '0123456789'
    $special = '!@#$%^&*()-_=+[]{}|;:,.<>?'
    $allChars = $upper + $lower + $digits + $special

    # Ensure at least one from each category
    $password = @(
        $upper[(Get-Random -Maximum $upper.Length)]
        $lower[(Get-Random -Maximum $lower.Length)]
        $digits[(Get-Random -Maximum $digits.Length)]
        $special[(Get-Random -Maximum $special.Length)]
    )

    for ($i = $password.Count; $i -lt $Length; $i++) {
        $password += $allChars[(Get-Random -Maximum $allChars.Length)]
    }

    # Shuffle
    $password = ($password | Sort-Object { Get-Random }) -join ''
    return $password
}
