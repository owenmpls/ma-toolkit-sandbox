#Requires -Version 7.4
<#
.SYNOPSIS
    Sandbox cleanup script for the analytics ingestion orchestrator cutover.
    Deletes old tier-prefixed landing data, disables ADF triggers, and documents
    DLT full-refresh steps.

.DESCRIPTION
    Run this script after deploying the new ingestion orchestrator and before
    running the first ingestion job. This script:
    1. Deletes old landing data (core/, core_enrichment/, enrichment/ directories)
    2. Disables ADF triggers (so ADF doesn't compete with the orchestrator)
    3. Prints instructions for DLT full-refresh (must be done manually in Databricks)

.PARAMETER StorageAccountName
    Name of the ADLS Gen2 storage account (e.g., stanalyticsabc123).

.PARAMETER ResourceGroupName
    Azure resource group containing analytics resources.

.PARAMETER DataFactoryName
    Name of the Azure Data Factory to disable triggers on.

.PARAMETER SkipAdf
    Skip ADF trigger disabling (if ADF was already removed).

.PARAMETER DryRun
    Show what would be deleted without actually deleting.
#>

param(
    [Parameter(Mandatory)][string]$StorageAccountName,
    [Parameter(Mandatory)][string]$ResourceGroupName,
    [string]$DataFactoryName,
    [switch]$SkipAdf,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

Write-Host "`n=== Analytics Sandbox Cleanup ===" -ForegroundColor Cyan

# --- Step 1: Delete old tier-prefixed landing data ---
Write-Host "`n[1/3] Cleaning up old landing container structure..." -ForegroundColor Yellow

$oldDirectories = @('core', 'core_enrichment', 'enrichment')
$ctx = New-AzStorageContext -StorageAccountName $StorageAccountName -UseConnectedAccount

foreach ($dir in $oldDirectories) {
    Write-Host "  Checking $dir/..." -NoNewline
    $blobs = Get-AzStorageBlob -Container 'landing' -Prefix "$dir/" -Context $ctx -MaxCount 1
    if ($blobs) {
        if ($DryRun) {
            Write-Host " EXISTS (would delete)" -ForegroundColor DarkYellow
        } else {
            Write-Host " deleting..." -NoNewline
            # ADLS Gen2 directory delete via REST (recursive)
            $token = (Get-AzAccessToken -ResourceUrl "https://storage.azure.com/").Token
            $uri = "https://$StorageAccountName.dfs.core.windows.net/landing/${dir}?recursive=true"
            try {
                Invoke-RestMethod -Uri $uri -Method DELETE -Headers @{
                    'Authorization' = "Bearer $token"
                    'x-ms-version'  = '2021-08-06'
                }
                Write-Host " DELETED" -ForegroundColor Green
            } catch {
                if ($_.Exception.Response.StatusCode -eq 404) {
                    Write-Host " NOT FOUND (already clean)" -ForegroundColor DarkGray
                } else {
                    Write-Host " FAILED: $($_.Exception.Message)" -ForegroundColor Red
                }
            }
        }
    } else {
        Write-Host " not found (already clean)" -ForegroundColor DarkGray
    }
}

# --- Step 2: Disable ADF triggers ---
if (-not $SkipAdf -and $DataFactoryName) {
    Write-Host "`n[2/3] Disabling ADF triggers..." -ForegroundColor Yellow

    $triggers = @('tr_core', 'tr_core_enrichment', 'tr_enrichment', 'tr_dlt')
    foreach ($trigger in $triggers) {
        Write-Host "  Disabling $trigger..." -NoNewline
        if ($DryRun) {
            Write-Host " (dry run)" -ForegroundColor DarkYellow
        } else {
            try {
                Stop-AzDataFactoryV2Trigger `
                    -ResourceGroupName $ResourceGroupName `
                    -DataFactoryName $DataFactoryName `
                    -Name $trigger `
                    -Force
                Write-Host " DISABLED" -ForegroundColor Green
            } catch {
                Write-Host " SKIPPED: $($_.Exception.Message)" -ForegroundColor DarkYellow
            }
        }
    }
} else {
    Write-Host "`n[2/3] Skipping ADF trigger disabling" -ForegroundColor DarkGray
}

# --- Step 3: Print DLT full-refresh instructions ---
Write-Host "`n[3/3] DLT Full Refresh Required" -ForegroundColor Yellow
Write-Host @"

  The DLT pipelines need a full refresh to rebuild bronze and silver tables
  with the new schema (no _schedule_tier column, new landing paths).

  Steps (in Databricks UI or via CLI):

  1. Open the bronze DLT pipeline in Databricks Workflows
  2. Click "Full refresh all" to reset checkpoints and reprocess all data
  3. Wait for bronze pipeline to complete
  4. Open the silver DLT pipeline
  5. Click "Full refresh all" to rebuild silver tables from updated bronze
  6. Wait for silver pipeline to complete

  Alternatively, via Databricks CLI:

    # Get pipeline IDs
    databricks pipelines list-pipelines --filter "name LIKE '%bronze%'"
    databricks pipelines list-pipelines --filter "name LIKE '%silver%'"

    # Trigger full refresh
    databricks pipelines start-update --pipeline-id <bronze-pipeline-id> --full-refresh
    # Wait for completion, then:
    databricks pipelines start-update --pipeline-id <silver-pipeline-id> --full-refresh

"@ -ForegroundColor White

if ($DryRun) {
    Write-Host "`n=== DRY RUN COMPLETE (no changes made) ===" -ForegroundColor Cyan
} else {
    Write-Host "`n=== CLEANUP COMPLETE ===" -ForegroundColor Green
    Write-Host "Next steps:" -ForegroundColor White
    Write-Host "  1. Run DLT full refresh (see instructions above)"
    Write-Host "  2. Deploy the ingestion orchestrator Function App"
    Write-Host "  3. Trigger a manual ingestion run to populate landing data"
    Write-Host "  4. Run DLT again to process the new data into bronze/silver"
}
