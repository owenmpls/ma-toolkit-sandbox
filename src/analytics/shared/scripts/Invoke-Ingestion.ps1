#Requires -Version 7.4

param()

# --- Graceful cancellation ---
$script:Running = $true
[Console]::TreatControlCAsInput = $false
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
    $script:Running = $false
}
# --- Load shared modules ---
$modulesPath = Join-Path $PSScriptRoot 'modules'
Import-Module (Join-Path $modulesPath 'LogHelper.psm1') -Force
Import-Module (Join-Path $modulesPath 'RetryHelper.psm1') -Force
Import-Module (Join-Path $modulesPath 'KeyVaultHelper.psm1') -Force
Import-Module (Join-Path $modulesPath 'WorkerPool.psm1') -Force
Import-Module (Join-Path $modulesPath 'ReportHelper.psm1') -Force -ErrorAction SilentlyContinue
# StorageHelper or StorageHelperRest is loaded by Connect-ToService.ps1

# --- Read environment ---
$tenantKey = $env:TENANT_KEY
$scheduleTier = $env:SCHEDULE_TIER
$kvName = $env:KEYVAULT_NAME
$storageAcct = $env:STORAGE_ACCOUNT_NAME
$maxParallelism = [int]($env:MAX_PARALLELISM ?? '10')
$containerName = $env:LANDING_CONTAINER ?? 'landing'
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)

Write-Log "Starting ingestion run=$runId tenant=$tenantKey tier=$scheduleTier"

# --- Authenticate to Azure (managed identity) ---
Write-Log "Connecting to Azure with managed identity"
Connect-AzAccount -Identity -WarningAction SilentlyContinue | Out-Null
Write-Log "Azure authentication successful"

# --- Load tenant registry ---
Write-Log "Loading tenant registry from Key Vault '$kvName'"
$registry = Get-TenantRegistry -VaultName $kvName
$tenant = $registry.tenants | Where-Object { $_.tenant_key -eq $tenantKey }
if (-not $tenant) { throw "Tenant '$tenantKey' not found in registry" }
if (-not $tenant.enabled) {
    Write-Log "Tenant '$tenantKey' is disabled, exiting" -Level WARN
    exit 0
}

# --- Load certificate ---
$certPath = $null
try {
    Write-Log "Loading certificate '$($tenant.cert_name)' from Key Vault"
    $certPath = Get-CertificateFromKeyVault -VaultName $kvName -CertName $tenant.cert_name

    # --- Connect to service (container-specific) ---
    $connectScript = Join-Path $PSScriptRoot 'Connect-ToService.ps1'
    . $connectScript -TenantConfig $tenant -CertificatePath $certPath

    # --- Discover entity modules ---
    $entitiesPath = Join-Path $PSScriptRoot 'entities'
    $entityModules = @{}
    Get-ChildItem $entitiesPath -Filter '*.psm1' | ForEach-Object {
        $mod = Import-Module $_.FullName -PassThru -Force
        $config = & "$($mod.Name)\Get-EntityConfig"
        $entityModules[$config.Name] = @{ Module = $mod; Config = $config }
    }

    Write-Log "Discovered $($entityModules.Count) entity modules"

    # --- Filter to entities for this tenant + tier ---
    $wantedEntities = switch ($scheduleTier) {
        'core'             { $tenant.core_entities }
        'core_enrichment'  { $tenant.core_enrichment_entities }
        'enrichment'       { $tenant.enrichment_entities }
        default            { throw "Unknown schedule tier: $scheduleTier" }
    }
    $toRun = $wantedEntities | Where-Object { $entityModules.ContainsKey($_) }
    Write-Log "Will process $($toRun.Count) entities: $($toRun -join ', ')"

    if ($toRun.Count -eq 0) {
        Write-Log "No entities to process for tier '$scheduleTier', exiting" -Level WARN
        exit 0
    }

    # --- Expose tenant-level config as environment variables for entity modules ---
    if ($tenant.sign_in_lookback_days) {
        $env:SIGN_IN_LOOKBACK_DAYS = $tenant.sign_in_lookback_days
    }
    if ($tenant.report_period) {
        $env:REPORT_PERIOD = $tenant.report_period
    }

    # --- Process each entity ---
    $date = Get-Date -Format 'yyyy-MM-dd'

    foreach ($entityName in $toRun) {
        if (-not $script:Running) {
            Write-Log "Cancellation requested, stopping entity processing" -Level WARN
            break
        }

        $entity = $entityModules[$entityName]
        $config = $entity.Config
        $entityStartTime = Get-Date -Format 'o'

        Write-Log "Processing entity '$entityName'" -Entity $entityName -TenantKey $tenantKey

        $basePath = "$scheduleTier/$($config.Name)/$tenantKey/$date"
        $recordCount = 0
        $phase2Count = 0
        $phase2Chunks = 0
        $phase2Skipped = 0
        $errors = @()
        $status = 'success'

        try {
            # --- Phase 1: Stream enumeration ---
            $localFile = Join-Path ([System.IO.Path]::GetTempPath()) "$($config.OutputFile)_${runId}.jsonl"
            $writer = [System.IO.StreamWriter]::new($localFile, $false, [System.Text.Encoding]::UTF8)
            $entityIds = [System.Collections.Generic.List[string]]::new()

            try {
                & "$($entity.Module.Name)\Invoke-Phase1" `
                    -Writer $writer `
                    -RecordCount ([ref]$recordCount) `
                    -EntityIds $entityIds
            }
            finally {
                $writer.Flush()
                $writer.Dispose()
            }

            Write-Log "Phase 1 complete: $recordCount records" -Entity $entityName -TenantKey $tenantKey

            # Upload Phase 1 file
            if ($recordCount -gt 0) {
                $blobPath = "$basePath/$($config.OutputFile)_${runId}.jsonl"
                Write-Log "Uploading Phase 1 to $blobPath" -Entity $entityName
                & $script:UploadFunction -StorageAccountName $storageAcct `
                    -ContainerName $containerName `
                    -BlobPath $blobPath `
                    -LocalFile $localFile
            }

            # Clean up local Phase 1 file
            if (Test-Path $localFile) { Remove-Item $localFile -Force }

            # --- Phase 2: Parallel enrichment (if applicable) ---
            if ($config.Phase2 -and $entityIds.Count -gt 0) {
                Write-Log "Starting Phase 2 with $($entityIds.Count) items, parallelism=$maxParallelism" -Entity $entityName

                $phase2Dir = Join-Path ([System.IO.Path]::GetTempPath()) "phase2_${entityName}_${runId}"
                New-Item -ItemType Directory -Path $phase2Dir -Force | Out-Null

                $phase2Params = @{
                    EntityIds       = $entityIds.ToArray()
                    OutputDirectory = $phase2Dir
                    RunId           = $runId
                    PoolSize        = $maxParallelism
                }
                if ($script:AuthConfig) { $phase2Params['AuthConfig'] = $script:AuthConfig }
                # Clone cert bytes — Phase 2 entities zero their copy as a security measure,
                # which would corrupt the shared reference for subsequent entities.
                if ($script:CertBytes)  { $phase2Params['CertBytes']  = [byte[]]$script:CertBytes.Clone() }

                $phase2Result = & "$($entity.Module.Name)\Invoke-Phase2" @phase2Params

                $phase2Count = $phase2Result.RecordCount
                $phase2Chunks = $phase2Result.ChunkCount
                $phase2Skipped = if ($phase2Result.SkippedCount) { $phase2Result.SkippedCount } else { 0 }

                Write-Log "Phase 2 complete: $phase2Count records in $phase2Chunks chunks ($phase2Skipped skipped)" -Entity $entityName

                if ($phase2Result.Errors -and $phase2Result.Errors.Count -gt 0) {
                    $errors += $phase2Result.Errors
                    Write-Log "Phase 2 had $($phase2Result.Errors.Count) error(s)" -Level WARN -Entity $entityName
                    foreach ($errMsg in $phase2Result.Errors) {
                        Write-Log "  $errMsg" -Level WARN -Entity $entityName
                    }
                }

                # Upload Phase 2 chunks
                Get-ChildItem $phase2Dir -Filter '*.jsonl' | ForEach-Object {
                    $chunkBlobPath = "$basePath/$($config.DetailType)/$($_.Name)"
                    & $script:UploadFunction -StorageAccountName $storageAcct `
                        -ContainerName $containerName `
                        -BlobPath $chunkBlobPath `
                        -LocalFile $_.FullName
                }

                # Clean up Phase 2 temp dir
                if (Test-Path $phase2Dir) { Remove-Item $phase2Dir -Recurse -Force }
            }
        }
        catch {
            $status = 'failed'
            $errors += $_.Exception.Message
            Write-Log "Entity '$entityName' failed: $($_.Exception.Message)" -Level ERROR -Entity $entityName -TenantKey $tenantKey
        }

        # --- Write and upload manifest ---
        $manifest = @{
            run_id              = $runId
            tenant_key          = $tenantKey
            tenant_id           = $tenant.tenant_id
            entity_type         = $config.Name
            schedule_tier       = $scheduleTier
            phase1_record_count = $recordCount
            phase2_record_count  = $phase2Count
            phase2_chunk_count   = $phase2Chunks
            phase2_skipped_count = $phase2Skipped
            started_at          = $entityStartTime
            completed_at        = (Get-Date -Format 'o')
            status              = $status
            errors              = $errors
        }

        $manifestJson = $manifest | ConvertTo-Json -Depth 3
        $manifestPath = Join-Path ([System.IO.Path]::GetTempPath()) "_manifest_${entityName}_${runId}.json"
        [System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.Encoding]::UTF8)

        $manifestBlobPath = "$basePath/_manifest_${runId}.json"
        & $script:UploadFunction -StorageAccountName $storageAcct `
            -ContainerName $containerName `
            -BlobPath $manifestBlobPath `
            -LocalFile $manifestPath

        if (Test-Path $manifestPath) { Remove-Item $manifestPath -Force }

        Write-Log "Entity '$entityName' completed with status=$status" -Entity $entityName -TenantKey $tenantKey
    }

    Write-Log "Ingestion run=$runId completed"
}
catch {
    Write-Log "FATAL: $($_.Exception.Message)" -Level ERROR
    Write-Log "Stack trace: $($_.ScriptStackTrace)" -Level ERROR
    exit 1
}
finally {
    # Certificate cleanup - secure zero-fill before deletion
    if ($certPath) { Remove-CertificateFile -Path $certPath }
}
