<#
.SYNOPSIS
    Sample custom functions demonstrating extensibility patterns.
.DESCRIPTION
    Each function shows the recommended pattern for a specific service type.
    Use these as templates when building custom migration functions.

    This module declares RequiredServices = @('activeDirectory', 'exchangeServer',
    'sharepointOnline', 'teams') — meaning ALL four services must be enabled for
    this module's functions to be registered. For production use, split functions
    into separate modules per service so capability gating works independently.
#>

function Set-SampleADAttribute {
    <#
    .SYNOPSIS
        Sample: Sets an AD attribute using the multi-forest connection pattern.
    .DESCRIPTION
        Demonstrates Get-ADForestConnection + Set-ADUser -Server -Credential.
        This is the standard pattern for all AD operations in the hybrid worker.
    .PARAMETER TargetForest
        The forest name (must match a configured forest in worker-config.json).
    .PARAMETER Identity
        The user identity (SamAccountName, DN, GUID, or SID).
    .PARAMETER AttributeName
        The AD attribute name to set.
    .PARAMETER AttributeValue
        The value to assign.
    .EXAMPLE
        # Job message JSON:
        # {
        #   "JobId": "sample-001",
        #   "FunctionName": "Set-SampleADAttribute",
        #   "Parameters": {
        #     "TargetForest": "corp.contoso.com",
        #     "Identity": "jdoe",
        #     "AttributeName": "extensionAttribute1",
        #     "AttributeValue": "SampleValue"
        #   }
        # }
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

    return [PSCustomObject]@{
        Identity  = $Identity
        Forest    = $TargetForest
        Attribute = $AttributeName
        Value     = $AttributeValue
        Success   = $true
    }
}

function Test-SampleExchangeMailbox {
    <#
    .SYNOPSIS
        Sample: Tests whether a remote mailbox exists in Exchange Server.
    .DESCRIPTION
        Demonstrates Exchange Server cmdlet usage pattern.
        Exchange Server session is established during session pool initialization.
    .PARAMETER Identity
        The mailbox identity to check.
    .EXAMPLE
        # Job message JSON:
        # {
        #   "JobId": "sample-002",
        #   "FunctionName": "Test-SampleExchangeMailbox",
        #   "Parameters": {
        #     "Identity": "jdoe@contoso.com"
        #   }
        # }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Identity
    )

    $mailbox = Get-RemoteMailbox -Identity $Identity -ErrorAction SilentlyContinue
    $exists = $null -ne $mailbox

    return [PSCustomObject]@{
        Identity = $Identity
        Exists   = $exists
        Guid     = if ($exists) { $mailbox.Guid.ToString() } else { $null }
    }
}

function Get-SampleSPOSiteInfo {
    <#
    .SYNOPSIS
        Sample: Retrieves SharePoint Online site information.
    .DESCRIPTION
        Demonstrates SPO cmdlet usage pattern.
        SPO connection is established during session pool initialization.
    .PARAMETER Url
        The URL of the site collection to query.
    .EXAMPLE
        # Job message JSON:
        # {
        #   "JobId": "sample-003",
        #   "FunctionName": "Get-SampleSPOSiteInfo",
        #   "Parameters": {
        #     "Url": "https://contoso.sharepoint.com/sites/mysite"
        #   }
        # }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Url
    )

    $site = Get-SPOSite -Identity $Url -ErrorAction Stop

    return [PSCustomObject]@{
        Url          = $site.Url
        Title        = $site.Title
        Owner        = $site.Owner
        StorageUsage = $site.StorageUsageCurrent
        StorageQuota = $site.StorageQuota
        Status       = $site.Status
    }
}

function Get-SampleTeamInfo {
    <#
    .SYNOPSIS
        Sample: Retrieves Microsoft Teams team information.
    .DESCRIPTION
        Demonstrates MicrosoftTeams cmdlet usage pattern.
        Teams connection is established during session pool initialization.
    .PARAMETER GroupId
        The Group ID (GUID) of the team to query.
    .EXAMPLE
        # Job message JSON:
        # {
        #   "JobId": "sample-004",
        #   "FunctionName": "Get-SampleTeamInfo",
        #   "Parameters": {
        #     "GroupId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
        #   }
        # }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$GroupId
    )

    $team = MicrosoftTeams\Get-Team -GroupId $GroupId -ErrorAction Stop

    return [PSCustomObject]@{
        GroupId     = $team.GroupId
        DisplayName = $team.DisplayName
        Description = $team.Description
        Visibility  = $team.Visibility
        Archived    = $team.Archived
    }
}
