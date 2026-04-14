function Get-EntityConfig {
    return @{
        Name         = 'exo_mailbox_statistics'
        Phase1       = $true
        Phase2       = $true
        ApiSource    = 'exo'
        OutputFile   = 'exo_mailbox_statistics'
        DetailType   = 'statistics'
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

    return Invoke-EntityPhase2 -EntityName 'exo_mailbox_statistics' `
        -EntityIds $EntityIds -OutputDirectory $OutputDirectory -RunId $RunId `
        -AuthConfig $AuthConfig -CertBytes $CertBytes -PoolSize $PoolSize `
        -ApiFamily 'exo' -FlushInterval 50 -WorkScript @'
param($ItemId, $AuthCfg, $PreFlight)
$stats = Get-EXOMailboxStatistics -Identity $ItemId -PropertySets All -ErrorAction Stop
# Dump entire object — ByteQuantifiedSize fields (TotalItemSize,
# TotalDeletedItemSize) serialize as structs, but TablesTotalSize
# is a plain bigint that the silver layer uses instead.
return $stats
'@
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
