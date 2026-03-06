function Get-EntityConfig {
    return @{
        Name         = 'onedrive_usage'
        ScheduleTier = 'enrichment'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'onedrive_usage'
        DetailType   = $null
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0

    # Graph Reports API returns CSV via redirect — save to temp file
    $tempCsv = Join-Path ([System.IO.Path]::GetTempPath()) "onedrive_usage_report_$([guid]::NewGuid().ToString('N').Substring(0,8)).csv"

    try {
        Invoke-MgGraphRequest -Method GET `
            -Uri "/v1.0/reports/getOneDriveUsageAccountDetail(period='D180')" `
            -OutputFilePath $tempCsv

        # Import CSV and normalize column names to PascalCase for JSONL
        Import-Csv $tempCsv | ForEach-Object {
            if (-not $script:Running) { return }

            $record = @{
                ReportRefreshDate    = $_.'Report Refresh Date'
                SiteUrl              = $_.'Site URL'
                OwnerDisplayName     = $_.'Owner Display Name'
                OwnerPrincipalName   = $_.'Owner Principal Name'
                IsDeleted            = $_.'Is Deleted'
                LastActivityDate     = $_.'Last Activity Date'
                FileCount            = [long]($_.'File Count')
                ActiveFileCount      = [long]($_.'Active File Count')
                StorageUsedBytes     = [long]($_.'Storage Used (Byte)')
                StorageAllocatedBytes = [long]($_.'Storage Allocated (Byte)')
                ReportPeriod         = $_.'Report Period'
            }
            $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($record.SiteUrl)
            $count++
            if ($count % 1000 -eq 0) { $Writer.Flush() }
        }
    }
    finally {
        if (Test-Path $tempCsv) { Remove-Item $tempCsv -Force }
    }

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param([string[]]$EntityIds, [string]$OutputDirectory, [string]$RunId, [int]$PoolSize)
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
