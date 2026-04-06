function Get-EntityConfig {
    return @{
        Name         = 'exo_mail_users'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'exo'
        OutputFile   = 'exo_mail_users'
        DetailType   = $null
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $extraProps = @(
        'DisplayName', 'Alias', 'PrimarySmtpAddress', 'EmailAddresses',
        'ExternalEmailAddress', 'HiddenFromAddressListsEnabled',
        'WhenCreated', 'WhenChanged', 'FirstName', 'LastName',
        'City', 'Company', 'Department', 'Manager', 'Office', 'Title',
        'Notes', 'Identity', 'DistinguishedName'
    )

    $count = 0
    Get-EXORecipient -RecipientTypeDetails MailUser,GuestMailUser -ResultSize Unlimited `
        -PropertySets Archive,Custom,MailboxMove,Policy `
        -Properties $extraProps | ForEach-Object {
        $Writer.WriteLine(($_ | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add($_.ExternalDirectoryObjectId)
        $count++
        if ($count % 1000 -eq 0) { $Writer.Flush() }
    }

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param([string[]]$EntityIds, [string]$OutputDirectory, [string]$RunId, [int]$PoolSize)
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
