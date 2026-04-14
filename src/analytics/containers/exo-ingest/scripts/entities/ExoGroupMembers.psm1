function Get-EntityConfig {
    return @{
        Name         = 'exo_group_members'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'exo'
        OutputFile   = 'exo_group_members'
        DetailType   = 'members'
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0

    # Enumerate distribution groups
    Get-DistributionGroup -ResultSize Unlimited | ForEach-Object {
        $record = @{
            Identity                   = $_.Identity
            ExternalDirectoryObjectId  = $_.ExternalDirectoryObjectId
            DisplayName                = $_.DisplayName
            PrimarySmtpAddress         = $_.PrimarySmtpAddress
            GroupType                  = 'DistributionGroup'
        }
        $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add("DG:$($_.ExternalDirectoryObjectId):$($_.Identity)")
        $count++
        if ($count % 1000 -eq 0) { $Writer.Flush() }
    }

    # Enumerate unified groups
    Get-UnifiedGroup -ResultSize Unlimited | ForEach-Object {
        $record = @{
            Identity                   = $_.Identity
            ExternalDirectoryObjectId  = $_.ExternalDirectoryObjectId
            DisplayName                = $_.DisplayName
            PrimarySmtpAddress         = $_.PrimarySmtpAddress
            GroupType                  = 'UnifiedGroup'
        }
        $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add("UG:$($_.ExternalDirectoryObjectId):$($_.Identity)")
        $count++
        if ($count % 1000 -eq 0) { $Writer.Flush() }
    }

    $Writer.Flush()
    $RecordCount.Value = $count
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

    return Invoke-EntityPhase2 -EntityName 'exo_group_members' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'exo' -FlushInterval 50 -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
$parts = $ItemId -split ':', 3
$groupType = $parts[0]
$groupObjectId = $parts[1]
$groupIdentity = $parts[2]

$members = if ($groupType -eq 'DG') {
    Get-DistributionGroupMember -Identity $groupIdentity -ResultSize Unlimited
} else {
    Get-UnifiedGroupLinks -Identity $groupIdentity -LinkType Members -ResultSize Unlimited
}

$records = @()
foreach ($member in $members) {
    $records += @{
        groupIdentity  = $groupIdentity
        groupObjectId  = $groupObjectId
        groupType      = $groupType
        memberName     = $member.Name
        memberObjectId = $member.ExternalDirectoryObjectId
        memberType     = $member.RecipientType
        primarySmtp    = $member.PrimarySmtpAddress
    }
}
return $records
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
