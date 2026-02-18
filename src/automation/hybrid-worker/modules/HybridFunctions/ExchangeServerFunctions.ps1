<#
.SYNOPSIS
    Exchange Server Management Shell functions for the Hybrid Worker.
.DESCRIPTION
    Provides remote mailbox management and validation functions.
    These run in PS 5.1 PSSessions with Exchange Server cmdlets imported.
#>

function New-ExchangeRemoteMailbox {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Identity,
        [Parameter(Mandatory)] [string]$RemoteRoutingAddress,
        [string]$Alias
    )

    # Skeleton — implement based on migration requirements
    $params = @{
        Identity              = $Identity
        RemoteRoutingAddress  = $RemoteRoutingAddress
    }
    if ($Alias) { $params['Alias'] = $Alias }

    Enable-RemoteMailbox @params -ErrorAction Stop
    $mailbox = Get-RemoteMailbox -Identity $Identity -ErrorAction Stop

    return [PSCustomObject]@{
        Identity             = $mailbox.Identity
        RemoteRoutingAddress = $mailbox.RemoteRoutingAddress.ToString()
        PrimarySmtpAddress   = $mailbox.PrimarySmtpAddress.ToString()
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
