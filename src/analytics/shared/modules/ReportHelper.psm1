function ConvertTo-SnakeCaseKey {
    <#
    .SYNOPSIS
        Converts a CSV column header to snake_case.
    .EXAMPLE
        ConvertTo-SnakeCaseKey "Storage Used (Byte)"  # → "storage_used_byte"
        ConvertTo-SnakeCaseKey "Page View Count"       # → "page_view_count"
    #>
    param([Parameter(Mandatory)][string]$Name)

    $Name = $Name -replace '\s*\(', '_' -replace '\)', '' -replace '\s+', '_'
    return $Name.ToLower().TrimEnd('_')
}

function Invoke-GraphReport {
    <#
    .SYNOPSIS
        Downloads a Microsoft Graph usage report (CSV), normalizes column names
        to snake_case, and writes each row as a JSON line to the provided StreamWriter.
    .DESCRIPTION
        Graph Reports APIs return a 302 redirect to a pre-authenticated CSV download URL.
        Invoke-MgGraphRequest with -OutputFilePath follows the redirect and saves the CSV.
        This function parses the CSV, normalizes headers, and emits JSONL.
    #>
    param(
        [Parameter(Mandatory)][string]$ReportUri,
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds,
        [Parameter(Mandatory)][string]$EntityIdColumn
    )

    $count = 0
    $tempCsv = Join-Path ([System.IO.Path]::GetTempPath()) "report_$([guid]::NewGuid().ToString('N').Substring(0,8)).csv"

    try {
        # Download CSV — Invoke-MgGraphRequest follows the 302 redirect
        Invoke-MgGraphRequest -Method GET -Uri $ReportUri -OutputFilePath $tempCsv -ErrorAction Stop

        $rows = Import-Csv -Path $tempCsv -ErrorAction Stop

        # Build header mapping once from first row
        $headerMap = $null

        foreach ($row in $rows) {
            if (-not $headerMap) {
                $headerMap = @{}
                foreach ($prop in $row.PSObject.Properties) {
                    $headerMap[$prop.Name] = ConvertTo-SnakeCaseKey $prop.Name
                }
            }

            $record = [ordered]@{}
            foreach ($prop in $row.PSObject.Properties) {
                $snakeKey = $headerMap[$prop.Name]
                $record[$snakeKey] = $prop.Value
            }

            $Writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))

            # Collect entity IDs for manifest / Phase 2 (if ever needed)
            $idValue = $row.$EntityIdColumn
            if ($idValue) {
                $EntityIds.Add($idValue)
            }

            $count++
            if ($count % 1000 -eq 0) { $Writer.Flush() }
        }

        $Writer.Flush()
    }
    finally {
        if (Test-Path $tempCsv) { Remove-Item $tempCsv -Force }
    }

    $RecordCount.Value = $count
}

Export-ModuleMember -Function ConvertTo-SnakeCaseKey, Invoke-GraphReport
