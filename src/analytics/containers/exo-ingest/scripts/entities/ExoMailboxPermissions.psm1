function Get-EntityConfig {
    return @{
        Name         = 'exo_mailbox_permissions'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'exo'
        OutputFile   = 'exo_mailbox_permissions'
        DetailType   = 'permissions'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # Lightweight enumeration — collect ExchangeGuid values only.
    # No records written (Phase 1 upload is skipped when $RecordCount -eq 0).
    Get-EXOMailbox -PropertySets StatisticsSeed -ResultSize Unlimited | ForEach-Object {
        $EntityIds.Add($_.ExchangeGuid.ToString())
    }

    $RecordCount.Value = 0
}

function Invoke-Phase2 {
    param(
        [Parameter(Mandatory)][string[]]$EntityIds,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][hashtable]$AuthConfig,
        [Parameter(Mandatory)][byte[]]$CertBytes,
        [int]$PoolSize = 10
    )

    return Invoke-EntityPhase2 -EntityName 'exo_mailbox_permissions' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'exo' -FlushInterval 50 -PreFlightScript @'
param($AuthCfg)
return @{ HasSendAsCmdlet = $null -ne (Get-Command 'Get-RecipientPermission' -ErrorAction SilentlyContinue) }
'@ -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
# 1. Get mailbox metadata (RecipientTypeDetails + GrantSendOnBehalfTo)
$mbx = Get-EXOMailbox -Identity $ItemId -PropertySets Minimum `
    -Properties RecipientTypeDetails, GrantSendOnBehalfTo -ErrorAction Stop

# 2. Get mailbox permissions (FullAccess, ReadPermission, etc.)
$mbxPerms = Get-MailboxPermission -Identity $ItemId -ErrorAction Stop |
    Where-Object { $_.User -ne 'NT AUTHORITY\SELF' -and $_.User -ne 'SELF' }

# 3. Get recipient permissions (SendAs) — gracefully degrade if unavailable
$sendAsPerms = @()
if ($PreFlight.HasSendAsCmdlet) {
    try {
        $sendAsPerms = @(Get-RecipientPermission -Identity $ItemId -ErrorAction Stop |
            Where-Object { $_.Trustee -ne 'NT AUTHORITY\SELF' -and $_.Trustee -ne 'SELF' })
    }
    catch {
        # SendAs lookup failed for this mailbox — continue without it
    }
}

# Build mailbox permission entries
$mailboxPermissions = @()
foreach ($p in $mbxPerms) {
    $mailboxPermissions += @{
        trustee      = $p.User
        accessRights = @($p.AccessRights)
        isInherited  = [bool]$p.IsInherited
        deny         = [bool]$p.Deny
    }
}

# Build SendAs permission entries
$sendAsPermissions = @()
foreach ($p in $sendAsPerms) {
    $sendAsPermissions += @{
        trustee      = $p.Trustee
        accessRights = @($p.AccessRights)
        isInherited  = [bool]$p.IsInherited
    }
}

# SendOnBehalf — array of DN strings from the mailbox object
$sendOnBehalfTo = @()
if ($mbx.GrantSendOnBehalfTo) {
    $sendOnBehalfTo = @($mbx.GrantSendOnBehalfTo)
}

$record = @{
    exchangeGuid         = $ItemId
    userPrincipalName    = $mbx.UserPrincipalName
    displayName          = $mbx.DisplayName
    recipientTypeDetails = $mbx.RecipientTypeDetails
    primarySmtpAddress   = $mbx.PrimarySmtpAddress
    mailboxPermissions   = $mailboxPermissions
    sendAsPermissions    = $sendAsPermissions
    sendOnBehalfTo       = $sendOnBehalfTo
    hasDelegates         = ($mailboxPermissions.Count -gt 0 -or $sendAsPermissions.Count -gt 0 -or $sendOnBehalfTo.Count -gt 0)
    hasFullAccess        = ($mailboxPermissions | Where-Object { $_.accessRights -contains 'FullAccess' }).Count -gt 0
    hasSendAs            = $sendAsPermissions.Count -gt 0
    hasSendOnBehalf      = $sendOnBehalfTo.Count -gt 0
    permissionCount      = $mailboxPermissions.Count + $sendAsPermissions.Count + $sendOnBehalfTo.Count
}
return $record
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
