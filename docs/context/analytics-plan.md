# M365 Analytics Ingestion — Multi-Tenant Rebuild Handoff

> **Purpose**: Self-contained handoff document for rebuilding the M365 analytics ingestion subsystem in a separate repo. Covers architecture, patterns, entity catalog, and implementation guidance. No access to the original repos is needed.

> **Scope**: Bronze → Silver only. Entity tables only — no identity mapping, no Gold layer. Multi-tenant architecture supporting dozens of tenants with per-tenant entity selection and scheduling.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Tenant Registry Design](#2-tenant-registry-design)
3. [Container Strategy](#3-container-strategy)
4. [Entity Catalog with Schedule Tiers](#4-entity-catalog-with-schedule-tiers)
5. [Two-Phase Worker Pattern](#5-two-phase-worker-pattern)
6. [Modular Entity Pattern](#6-modular-entity-pattern)
7. [ADLS Landing Zone Structure](#7-adls-landing-zone-structure)
8. [Databricks Bronze Pipeline](#8-databricks-bronze-pipeline)
9. [Databricks Silver Pipeline](#9-databricks-silver-pipeline)
10. [Orchestration (ADF)](#10-orchestration-adf)
11. [Infrastructure Guidance](#11-infrastructure-guidance)
12. [Repo Structure](#12-repo-structure)
13. [Implementation Sequence](#13-implementation-sequence)
14. [What to Keep vs Replace](#14-what-to-keep-vs-replace)

---

## 1. Architecture Overview

### End-to-End Flow

```
Tenant Registry (Key Vault JSON)
        │
        ▼
   Azure Data Factory
   ┌─────────────────────────────────────────────┐
   │ pl_orchestrate_core (daily)                  │
   │ pl_orchestrate_enrichment (weekly)           │
   │   1. Read tenant registry                    │
   │   2. Filter enabled tenants + schedule match │
   │   3. ForEach tenant (parallel):              │
   │        ForEach container type (parallel):    │
   │          pl_run_container_job(...)            │
   │   4. After all containers: pl_run_dlt(...)   │
   └─────────────────────────────────────────────┘
        │
        ▼
   Azure Container Apps Jobs
   ┌──────────┐ ┌──────────┐ ┌──────────┐
   │  graph   │ │   exo    │ │   spo    │
   │ ingest   │ │ ingest   │ │ ingest   │
   │ (Entra,  │ │ (EXO     │ │ (PnP     │
   │  Intune) │ │  cmdlets)│ │  PS)     │
   └──────────┘ └──────────┘ └──────────┘
        │              │            │
        ▼              ▼            ▼
   ADLS Gen2 (JSONL files per tenant/entity/date)
        │
        ▼
   Databricks DLT
   ┌────────────────────────────────────┐
   │ Bronze: Auto Loader → raw tables  │
   │ Silver: SCD Type 1 → clean tables │
   └────────────────────────────────────┘
```

### Key Principles

| Principle | Description |
|-----------|-------------|
| **Tenant-as-config** | Adding a tenant = adding a JSON entry + creating an app registration/cert. No code changes, no new pipelines, no new tables. |
| **Streaming writes** | JSONL via `StreamWriter` with periodic flush. Never buffer entire datasets in memory. |
| **Two-phase workers** | Phase 1 streams enumeration serially. Phase 2 enriches in parallel via RunspacePool. |
| **Schedule tiers** | Core entities run daily (fast, streaming-only). Enrichment entities run weekly (slow, parallel detail fetching). |
| **Modular entities** | Each entity is a self-contained PowerShell module with a standard interface. `Invoke-Ingestion.ps1` discovers and runs matching entities based on tenant config + schedule tier. |
| **Single table per entity** | Bronze and Silver have ONE table per entity type with `tenant_key`/`tenant_id` columns — not one table per tenant. |

---

## 2. Tenant Registry Design

### Schema

Stored as a Key Vault secret (`tenant-registry`) containing JSON:

```json
{
  "tenants": [
    {
      "tenant_key": "contoso",
      "tenant_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
      "display_name": "Contoso Corp",
      "organization": "contoso.onmicrosoft.com",
      "client_id": "11111111-2222-3333-4444-555555555555",
      "cert_name": "cert-contoso",
      "admin_url": "https://contoso-admin.sharepoint.com",
      "enabled": true,
      "core_entities": [
        "entra_users",
        "entra_groups",
        "entra_devices",
        "exo_mailboxes",
        "exo_distribution_groups",
        "exo_unified_groups",
        "spo_sites",
        "intune_devices"
      ],
      "enrichment_entities": [
        "exo_mailbox_statistics",
        "exo_folder_statistics",
        "spo_site_details",
        "intune_device_details"
      ],
      "core_schedule": "daily",
      "enrichment_schedule": "weekly",
      "max_parallelism": 10
    },
    {
      "tenant_key": "fabrikam",
      "tenant_id": "ffffffff-gggg-hhhh-iiii-jjjjjjjjjjjj",
      "display_name": "Fabrikam Inc",
      "organization": "fabrikam.onmicrosoft.com",
      "client_id": "66666666-7777-8888-9999-aaaaaaaaaaaa",
      "cert_name": "cert-fabrikam",
      "admin_url": "https://fabrikam-admin.sharepoint.com",
      "enabled": true,
      "core_entities": [
        "entra_users",
        "entra_groups",
        "exo_mailboxes"
      ],
      "enrichment_entities": [
        "exo_mailbox_statistics"
      ],
      "core_schedule": "daily",
      "enrichment_schedule": "weekly",
      "max_parallelism": 5
    }
  ]
}
```

### Field Reference

| Field | Type | Description |
|-------|------|-------------|
| `tenant_key` | string | Short identifier used in ADLS paths and SCD keys. Must be unique, lowercase, no spaces. |
| `tenant_id` | GUID | Azure AD tenant ID. |
| `display_name` | string | Human-readable name for logging/monitoring. |
| `organization` | string | Tenant domain (e.g. `contoso.onmicrosoft.com`). Used by EXO and PnP auth. |
| `client_id` | GUID | App registration (service principal) client ID in the tenant. |
| `cert_name` | string | Key Vault certificate name for this tenant's app registration. |
| `admin_url` | string | SharePoint admin URL. Only needed if `spo_sites` or `spo_site_details` are in the entity lists. |
| `enabled` | bool | Set to `false` to skip this tenant without removing the config entry. |
| `core_entities` | string[] | Entities to ingest on the core (daily) schedule. Must match entity module names. |
| `enrichment_entities` | string[] | Entities to ingest on the enrichment (weekly) schedule. |
| `core_schedule` | string | `"daily"` — frequency for core entities. |
| `enrichment_schedule` | string | `"weekly"` — frequency for enrichment entities. |
| `max_parallelism` | int | Maximum RunspacePool size for Phase 2 enrichment workers (1–20). |

### Onboarding Workflow

```
1. Create app registration in tenant's Azure AD
2. Grant required API permissions (see Entity Catalog §4)
3. Generate certificate, upload public key to app registration
4. Import certificate to shared Key Vault as "cert-{tenant_key}"
5. Add tenant entry to the tenant-registry secret in Key Vault
6. Set enabled: true
7. Done — next scheduled run picks up the new tenant automatically
```

### How Containers Consume Tenant Config

Each container receives `TENANT_KEY` as an environment variable from ADF. At startup:

```powershell
# 1. Read tenant registry from Key Vault
$registryJson = Get-AzKeyVaultSecret -VaultName $env:KEYVAULT_NAME -Name "tenant-registry" -AsPlainText
$registry = $registryJson | ConvertFrom-Json

# 2. Find this tenant's config
$tenantConfig = $registry.tenants | Where-Object { $_.tenant_key -eq $env:TENANT_KEY }

# 3. Load certificate for authentication
$certPath = Get-CertificateFromKeyVault -VaultName $env:KEYVAULT_NAME -CertName $tenantConfig.cert_name

# 4. Determine which entities to run based on schedule tier
$entitiesToRun = if ($env:SCHEDULE_TIER -eq "core") {
    $tenantConfig.core_entities
} else {
    $tenantConfig.enrichment_entities
}

# 5. Filter to entities this container handles (e.g. graph container only runs entra_* and intune_*)
$myEntities = $entitiesToRun | Where-Object { $entityModules.ContainsKey($_) }
```

---

## 3. Container Strategy

### Why Three Containers

The M365 PowerShell SDK ecosystem has **irreconcilable module dependency conflicts**:

| Container | Key Modules | OData Version | Storage Upload Method |
|-----------|------------|---------------|----------------------|
| **graph-ingest** | Microsoft.Graph.Authentication, .Users, .Groups, .Identity.DirectoryManagement, .DeviceManagement | 7.21.0 | Az.Storage (`Set-AzStorageBlobContent`) |
| **exo-ingest** | ExchangeOnlineManagement v3.4+ | 7.15.0 | REST API (no Az.Storage — it pulls in OData 7.21.0) |
| **spo-ingest** | PnP.PowerShell | Own dependency tree | Az.Storage or REST API |

**The core conflict**: ExchangeOnlineManagement requires `Microsoft.OData.Core 7.15.0`. Microsoft.Graph SDK requires `7.21.0`. Loading both in the same process causes .NET assembly resolution failures at runtime. Separate containers ensure each module uses its native version.

**EXO's storage workaround**: Since `Az.Storage` transitively depends on OData assemblies that conflict with EXO, the EXO container uploads to ADLS via raw REST API calls against the blob endpoint, authenticating with Managed Identity tokens from the ACA identity endpoint.

### Base Image

All containers use `mcr.microsoft.com/powershell:lts-ubuntu-22.04` (PowerShell 7 on Linux).

### Container Responsibilities

Each container handles **all of its entities for ONE tenant per invocation**. ADF launches separate container jobs for each tenant × container-type combination.

**graph-ingest handles**:
- `entra_users`, `entra_groups`, `entra_devices` (core)
- `intune_devices` (core)
- `intune_device_details`, `intune_autopilot` (enrichment)

**exo-ingest handles**:
- `exo_mailboxes`, `exo_distribution_groups`, `exo_unified_groups` (core)
- `exo_mailbox_statistics`, `exo_folder_statistics` (enrichment)

**spo-ingest handles**:
- `spo_sites` (core)
- `spo_site_details` (enrichment)

### Shared Modules (copied into each container)

| Module | Purpose |
|--------|---------|
| `KeyVaultHelper.psm1` | Certificate retrieval from Key Vault with expiry validation, secret retrieval, tenant registry loading |
| `StorageHelper.psm1` | ADLS upload (Az.Storage variant for graph/spo, REST variant for exo) |
| `WorkerPool.psm1` | RunspacePool creation, work partitioning, dispatch, and polling |
| `RetryHelper.psm1` | `Invoke-WithRetry` with auth reconnection + throttle backoff + jitter |
| `LogHelper.psm1` | Structured logging with timestamps and levels |

---

## 4. Entity Catalog with Schedule Tiers

### Core Tier — Daily, Phase 1 Only (Streaming Enumeration)

| Entity | Container | API / Cmdlet | Key Fields | Required Permissions |
|--------|-----------|-------------|------------|---------------------|
| `entra_users` | graph | `GET /v1.0/users` with `$select` (30+ fields incl. on-prem sync attrs, licenses, phone) | `id`, `userPrincipalName`, `mail`, `assignedLicenses` | `User.Read.All` |
| `entra_groups` | graph | `GET /v1.0/groups` with `$select` | `id`, `displayName`, `groupTypes`, `mailEnabled`, `securityEnabled` | `Group.Read.All` |
| `entra_devices` | graph | `GET /beta/devices?$expand=registeredOwners,registeredUsers` | `deviceId`, `displayName`, `operatingSystem`, `registeredOwners` | `Device.Read.All` |
| `exo_mailboxes` | exo | `Get-EXOMailbox -PropertySets All -ResultSize Unlimited` | `ExchangeGuid`, `PrimarySmtpAddress`, `RecipientTypeDetails` | Exchange app-only auth (certificate) |
| `exo_distribution_groups` | exo | `Get-DistributionGroup -ResultSize Unlimited` | `ExchangeGuid`, `PrimarySmtpAddress`, `GroupType` | Exchange app-only auth |
| `exo_unified_groups` | exo | `Get-UnifiedGroup -ResultSize Unlimited` | `ExchangeGuid`, `PrimarySmtpAddress`, `AccessType` | Exchange app-only auth |
| `spo_sites` | spo | `Get-PnPTenantSite -IncludeOneDriveSites -Detailed` | `SiteId`, `Url`, `Template`, `StorageUsageCurrent` | `Sites.FullControl.All` (SharePoint), `Sites.Read.All` + `GroupMember.Read.All` (Graph) |
| `intune_devices` | graph | `GET /beta/deviceManagement/managedDevices` | `id`, `deviceName`, `azureADDeviceId`, `complianceState` | `DeviceManagementManagedDevices.Read.All` |

### Enrichment Tier — Weekly, Phase 2 RunspacePool (Parallel Detail Fetching)

| Entity | Container | API / Cmdlet | Phase 1 Source | Phase 2 Operation | Required Permissions |
|--------|-----------|-------------|---------------|-------------------|---------------------|
| `exo_mailbox_statistics` | exo | `Get-EXOMailboxStatistics` | Mailbox `ExchangeGuid` list from Phase 1 | One call per mailbox in parallel | Exchange app-only auth |
| `exo_folder_statistics` | exo | `Get-EXOMailboxFolderStatistics` | Mailbox `ExchangeGuid` list from Phase 1 | One call per mailbox (expensive — opt-in) | Exchange app-only auth |
| `spo_site_details` | spo | Per-site PnP calls (see below) | Site URL list from Phase 1 | Connect per site, collect details | `Sites.FullControl.All` |
| `intune_device_details` | graph | `GET /beta/deviceManagement/managedDevices` with filter | Device ID list from Phase 1 | One filtered call per device | `DeviceManagementManagedDevices.Read.All` |
| `intune_autopilot` | graph | `GET /beta/deviceManagement/windowsAutopilotDeviceIdentities` | Serial enumeration (no Phase 1 dependency) | Serial streaming, no RunspacePool | `DeviceManagementServiceConfig.Read.All` |

### SPO Site Detail Collection (Phase 2 per-site)

Each RunspacePool worker connects to one site and collects:

```
1. Get-PnPFolderStorageMetric -FolderSiteRelativeUrl "/"
   → TotalFileCount, TotalFileStreamSize, TotalSize

2. Get-PnPSiteSensitivityLabel
   → Sensitivity label assignment

3. Get-PnPSiteCollectionAdmin
   → LoginName, Title, Email

4. Get-PnPGroup + Get-PnPGroupMember (per group)
   → SharePoint group membership

5. Get-PnPList (filtered: BaseTemplate in 101, 700, 119)
   → Document library inventory: Title, Id, ItemCount, Created, LastModified

6. Get-PnPFolderSharingLink (per library, opt-in, expensive)
   → Sharing link inventory
```

### Permission Summary per Container

**graph-ingest app registration**:
- `User.Read.All`
- `Group.Read.All`
- `Device.Read.All`
- `Directory.Read.All`
- `DeviceManagementManagedDevices.Read.All`
- `DeviceManagementServiceConfig.Read.All`

**exo-ingest app registration**:
- Exchange app-only authentication with certificate
- Requires Exchange Administrator role assignment to the service principal

**spo-ingest app registration**:
- `Sites.FullControl.All` (SharePoint)
- `Sites.Read.All` (Graph)
- `GroupMember.Read.All` (Graph)

> **Note**: A single app registration per tenant can hold all permissions if preferred. The container separation is for module conflicts, not auth separation.

---

## 5. Two-Phase Worker Pattern

### Overview

```
Phase 1: Serial Streaming Enumeration
   │
   │  StreamWriter → JSONL file (one record per line)
   │  Collect entity IDs into List<string> for Phase 2
   │  Flush every N records (500–1000)
   │  Check $script:Running flag each iteration
   │
   ▼
Phase 2: RunspacePool Parallel Enrichment
   │
   │  Create RunspacePool(1, MaxParallelism)
   │  Pre-authenticate each runspace
   │  Round-robin partition entity IDs across runspaces
   │  Each runspace writes its own chunk-{NNN}.jsonl
   │  Inline Invoke-WithRetry per call
   │
   ▼
Upload + Manifest
```

### Phase 1 — Serial Streaming

```powershell
function Invoke-Phase1 {
    param(
        [System.IO.StreamWriter]$Writer,
        [ref]$RecordCount,
        [System.Collections.Generic.List[string]]$EntityIds  # for Phase 2 handoff
    )

    $flushInterval = 1000
    $count = 0

    # Example: EXO mailbox streaming
    Get-EXOMailbox -PropertySets All -ResultSize Unlimited | ForEach-Object {
        if (-not $script:Running) { return }

        $json = $_ | ConvertTo-Json -Compress -Depth 5
        $Writer.WriteLine($json)
        $EntityIds.Add($_.ExternalDirectoryObjectId)

        $count++
        if ($count % $flushInterval -eq 0) {
            $Writer.Flush()
            Write-Log "Phase1: Streamed $count records"
        }
    }

    $Writer.Flush()
    $RecordCount.Value = $count
}
```

**Critical patterns**:
- Use `[System.IO.StreamWriter]` — never buffer records in an array
- Use `[System.Collections.Generic.List[string]]` — never use `$array += $item` (O(n²) copy on every append)
- Pipe cmdlet output directly through `ForEach-Object` for true pipeline streaming
- Flush periodically to prevent data loss on crash
- Check `$script:Running` each iteration for graceful cancellation

### Phase 2 — RunspacePool Parallel Enrichment

#### Pool Initialization

```powershell
function New-WorkerPool {
    param(
        [string]$ModuleName,          # e.g. "ExchangeOnlineManagement"
        [int]$PoolSize,               # from tenant config max_parallelism
        [hashtable]$AuthConfig,       # tenant auth details
        [byte[]]$CertBytes            # certificate PFX bytes
    )

    # Create initial session state with module pre-loaded
    $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    $iss.ImportPSModule($ModuleName)

    # Create and open pool
    $pool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(
        1, $PoolSize, $iss, (Get-Host)
    )
    $pool.Open()

    # Pre-authenticate each runspace in parallel
    $authHandles = @()
    for ($i = 0; $i -lt $PoolSize; $i++) {
        $ps = [PowerShell]::Create().AddScript({
            param($Config, $CertBytes)
            # Store for reconnection inside work loop
            $global:ExportAuthConfig = $Config
            $global:ExportCertBytes = $CertBytes

            # Connect to service
            $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $CertBytes, [string]::Empty,
                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
            )
            Connect-ExchangeOnline -Certificate $cert `
                -AppId $Config.ClientId `
                -Organization $Config.Organization `
                -ShowBanner:$false
        }).AddArgument($AuthConfig).AddArgument($CertBytes)

        $ps.RunspacePool = $pool
        $authHandles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke() }
    }

    # Wait for all auth to complete
    foreach ($item in $authHandles) {
        $item.PowerShell.EndInvoke($item.Handle)
        $item.PowerShell.Dispose()
    }

    return $pool
}
```

> **PnP exception**: PnP.PowerShell requires per-site connections, so runspaces are NOT pre-authenticated. Instead, auth config (AppId, TenantDomain, CertificateBase64) is stored as `$global:ExportAuthConfig` and each runspace calls `Connect-PnPOnline -Url $siteUrl ...` inside its work loop, disconnecting after each site.

#### Work Partitioning — Round Robin

```powershell
function Split-WorkItems {
    param(
        [string[]]$Items,
        [int]$SliceCount
    )

    $slices = @()
    for ($i = 0; $i -lt $SliceCount; $i++) {
        $slices += , [System.Collections.Generic.List[string]]::new()
    }

    for ($i = 0; $i -lt $Items.Count; $i++) {
        $sliceIndex = $i % $SliceCount
        $slices[$sliceIndex].Add($Items[$i])
    }

    return $slices
}
```

#### Dispatch and Polling

```powershell
function Invoke-Phase2 {
    param(
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize,
        [System.Management.Automation.Runspaces.RunspacePool]$Pool
    )

    $slices = Split-WorkItems -Items $EntityIds -SliceCount $PoolSize

    # Dispatch one worker per slice
    $handles = @()
    for ($chunkIndex = 0; $chunkIndex -lt $slices.Count; $chunkIndex++) {
        $ps = [PowerShell]::Create().AddScript({
            param($Ids, $OutputDir, $ChunkNum, $RunId)

            $chunkFile = Join-Path $OutputDir "chunk-$($ChunkNum.ToString('000'))_${RunId}.jsonl"
            $writer = [System.IO.StreamWriter]::new($chunkFile, $false, [System.Text.Encoding]::UTF8)
            $processed = 0

            try {
                foreach ($id in $Ids) {
                    # Inline retry wrapper
                    $result = Invoke-WithRetry -ScriptBlock {
                        Get-EXOMailboxStatistics -ExchangeGuid $id -ErrorAction Stop
                    } -MaxRetries 5 -BaseDelay 2

                    if ($result) {
                        $writer.WriteLine(($result | ConvertTo-Json -Compress -Depth 5))
                        $processed++

                        if ($processed % 50 -eq 0) { $writer.Flush() }
                    }
                }
            }
            finally {
                $writer.Flush()
                $writer.Dispose()
            }

            return @{ ChunkIndex = $ChunkNum; Processed = $processed }
        }).AddArgument($slices[$chunkIndex]).AddArgument($OutputDirectory).AddArgument($chunkIndex).AddArgument($RunId)

        $ps.RunspacePool = $Pool
        $handles += @{ PowerShell = $ps; Handle = $ps.BeginInvoke(); ChunkIndex = $chunkIndex }
    }

    # Poll for completion
    $completed = [System.Collections.Generic.HashSet[int]]::new()
    while ($completed.Count -lt $handles.Count) {
        foreach ($item in $handles) {
            if ($completed.Contains($item.ChunkIndex)) { continue }
            if ($item.Handle.IsCompleted) {
                $output = $item.PowerShell.EndInvoke($item.Handle)
                $item.PowerShell.Dispose()
                $completed.Add($item.ChunkIndex) | Out-Null
                Write-Log "Phase2: Chunk $($item.ChunkIndex) completed — $($output.Processed) records"
            }
        }
        Start-Sleep -Seconds 5
    }
}
```

### Invoke-WithRetry Pattern

Inlined in each runspace scriptblock (runspaces don't share parent scope):

```powershell
function Invoke-WithRetry {
    param(
        [scriptblock]$ScriptBlock,
        [int]$MaxRetries = 5,
        [int]$BaseDelay = 2,
        [int]$MaxDelay = 120
    )

    $attempt = 0
    while ($true) {
        try {
            return & $ScriptBlock
        }
        catch {
            $attempt++
            $msg = $_.Exception.Message

            # Auth errors — reconnect and retry
            $isAuthError = $msg -match '401|Unauthorized|token expired|ACS50012'
            if ($isAuthError -and $attempt -le $MaxRetries) {
                Write-Verbose "Auth error, reconnecting (attempt $attempt)..."
                # Reconnect using stored globals
                $cfg = $global:ExportAuthConfig
                $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $global:ExportCertBytes, [string]::Empty,
                    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
                )
                Connect-ExchangeOnline -Certificate $cert `
                    -AppId $cfg.ClientId `
                    -Organization $cfg.Organization `
                    -ShowBanner:$false
                continue
            }

            # Throttle errors — exponential backoff with jitter
            $isThrottle = $msg -match '429|TooManyRequests|ServerBusy|throttl|MicroDelay|BackoffException'
            if ($isThrottle -and $attempt -le $MaxRetries) {
                # Check for Retry-After header
                $retryAfter = if ($_.Exception.Response.Headers['Retry-After']) {
                    [int]$_.Exception.Response.Headers['Retry-After']
                } else {
                    [math]::Min($BaseDelay * [math]::Pow(2, $attempt - 1), $MaxDelay)
                }
                $jitter = Get-Random -Minimum 0 -Maximum ([math]::Max(1, $retryAfter / 4))
                $delay = $retryAfter + $jitter
                Write-Verbose "Throttled, backing off ${delay}s (attempt $attempt)..."
                Start-Sleep -Seconds $delay
                continue
            }

            # SPO-specific: skippable errors (403, 404, locked sites)
            $isSkippable = $msg -match '403|404|site.*locked|AccessDenied|SiteNotFound'
            if ($isSkippable) {
                throw [System.InvalidOperationException]::new("SKIPPABLE: $msg", $_.Exception)
            }

            # Fatal — rethrow
            if ($attempt -gt $MaxRetries) { throw }
        }
    }
}
```

### Graceful Cancellation

```powershell
# At script entry
$script:Running = $true
[Console]::TreatControlCAsInput = $false
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
    $script:Running = $false
}
# Also handle Ctrl+C
trap {
    $script:Running = $false
    continue
}

# In Phase 1: check on every iteration
Get-EXOMailbox ... | ForEach-Object {
    if (-not $script:Running) { return }
    ...
}

# On cancel during Phase 2: drain in-flight work
if (-not $script:Running) {
    Write-Log "Cancellation requested, waiting up to 30s for in-flight work..."
    foreach ($item in $handles) {
        if (-not $item.Handle.IsCompleted) {
            $item.Handle.AsyncWaitHandle.WaitOne(30000) | Out-Null
        }
        $item.PowerShell.Dispose()
    }
    Save-ProgressCheckpoint -Path $checkpointPath -ProcessedIds $processedIds
}
```

### Progress Checkpointing (for resumable runs)

```powershell
function Save-ProgressCheckpoint {
    param([string]$Path, [string[]]$ProcessedIds)

    $checkpoint = @{
        ProcessedIds = $ProcessedIds
        Timestamp    = (Get-Date -Format 'o')
        Count        = $ProcessedIds.Count
    }
    $json = $checkpoint | ConvertTo-Json -Depth 3
    $tempPath = "$Path.tmp"
    [System.IO.File]::WriteAllText($tempPath, $json, [System.Text.Encoding]::UTF8)
    [System.IO.File]::Move($tempPath, $Path, $true)  # atomic rename
}

function Resume-FromCheckpoint {
    param([string]$Path, [string[]]$AllIds)

    if (-not (Test-Path $Path)) { return $AllIds }

    $checkpoint = Get-Content $Path -Raw | ConvertFrom-Json
    $done = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$checkpoint.ProcessedIds
    )
    Write-Log "Resuming: skipping $($done.Count) already-processed items"
    return $AllIds | Where-Object { -not $done.Contains($_) }
}
```

---

## 6. Modular Entity Pattern

### Standard Interface

Each entity is a `.psm1` file with three exported functions:

```powershell
# entities/EntraUsers.psm1

function Get-EntityConfig {
    return @{
        Name           = "entra_users"
        Container      = "graph"              # which container image handles this
        ScheduleTier   = "core"               # "core" or "enrichment"
        Phase1         = $true                # always true — stream enumeration
        Phase2         = $false               # true only for enrichment entities
        ApiSource      = "graph"              # "graph", "exo", or "spo"
        OutputFile     = "entra_users"        # base name for JSONL file
        DetailType     = $null                # subfolder name for Phase 2 chunks (null if no Phase 2)
    }
}

function Invoke-Phase1 {
    param(
        [System.IO.StreamWriter]$Writer,
        [ref]$RecordCount,
        [System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    $uri = '/v1.0/users?$select=id,userPrincipalName,mail,displayName,...&$top=999'

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($user in $response.value) {
            if (-not $script:Running) { return }
            $Writer.WriteLine(($user | ConvertTo-Json -Compress -Depth 5))
            $count++
        }
        if ($count % 1000 -eq 0) { $Writer.Flush() }
        $uri = $response['@odata.nextLink']
    } while ($uri)

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param(
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )
    # No-op for core entities without Phase 2
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
```

### Enrichment Entity Example (with Phase 2)

```powershell
# entities/ExoMailboxStatistics.psm1

function Get-EntityConfig {
    return @{
        Name           = "exo_mailbox_statistics"
        Container      = "exo"
        ScheduleTier   = "enrichment"
        Phase1         = $true                # Streams mailboxes to collect IDs
        Phase2         = $true                # Parallel stat collection
        ApiSource      = "exo"
        OutputFile     = "exo_mailboxes"      # Phase 1 reuses mailbox enumeration
        DetailType     = "mailbox_statistics"  # Phase 2 chunk subfolder
    }
}

function Invoke-Phase1 {
    param(
        [System.IO.StreamWriter]$Writer,
        [ref]$RecordCount,
        [System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    Get-EXOMailbox -PropertySets All -ResultSize Unlimited | ForEach-Object {
        if (-not $script:Running) { return }
        $Writer.WriteLine(($_ | ConvertTo-Json -Compress -Depth 5))
        $EntityIds.Add($_.ExternalDirectoryObjectId)
        $count++
        if ($count % 1000 -eq 0) { $Writer.Flush() }
    }
    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param(
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )

    $pool = New-WorkerPool -ModuleName "ExchangeOnlineManagement" `
                           -PoolSize $PoolSize `
                           -AuthConfig $script:AuthConfig `
                           -CertBytes $script:CertBytes

    try {
        $result = Invoke-ParallelWork -Pool $pool `
            -Items $EntityIds `
            -OutputDirectory $OutputDirectory `
            -RunId $RunId `
            -WorkScript {
                param($Id)
                Get-EXOMailboxStatistics -ExchangeGuid $Id -ErrorAction Stop
            }
        return $result  # @{ RecordCount = N; ChunkCount = M }
    }
    finally {
        $pool.Close()
        $pool.Dispose()
    }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
```

### SPO Site Details Entity (PnP — no pre-auth)

```powershell
# entities/SpoSiteDetails.psm1

function Invoke-Phase2 {
    param(
        [string[]]$EntityIds,      # Site URLs from Phase 1
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )

    # PnP: runspaces are NOT pre-authenticated — each connects per site
    $pool = New-WorkerPool -ModuleName "PnP.PowerShell" `
                           -PoolSize $PoolSize `
                           -AuthConfig $script:AuthConfig `
                           -CertBytes $null `
                           -SkipPreAuth   # PnP requires per-site connections

    try {
        $result = Invoke-ParallelWork -Pool $pool `
            -Items $EntityIds `
            -OutputDirectory $OutputDirectory `
            -RunId $RunId `
            -WorkScript {
                param($SiteUrl)
                $cfg = $global:ExportAuthConfig
                try {
                    Connect-PnPOnline -Url $SiteUrl `
                        -ClientId $cfg.AppId `
                        -Tenant $cfg.TenantDomain `
                        -CertificateBase64Encoded $cfg.CertificateBase64

                    $details = @{
                        SiteUrl           = $SiteUrl
                        StorageMetric     = Get-PnPFolderStorageMetric -FolderSiteRelativeUrl "/"
                        SensitivityLabel  = Get-PnPSiteSensitivityLabel
                        Admins            = @(Get-PnPSiteCollectionAdmin)
                        Groups            = @(Get-PnPGroup | ForEach-Object {
                            @{
                                Title   = $_.Title
                                Id      = $_.Id
                                Members = @(Get-PnPGroupMember -Group $_.Title)
                            }
                        })
                        DocumentLibraries = @(Get-PnPList | Where-Object {
                            $_.BaseTemplate -in @(101, 700, 119)
                        } | Select-Object Title, Id, ItemCount, Created, LastItemModifiedDate)
                    }
                    return ($details | ConvertTo-Json -Compress -Depth 10)
                }
                catch {
                    $msg = $_.Exception.Message
                    if ($msg -match '403|404|site.*locked|AccessDenied|SiteNotFound') {
                        return (@{
                            SiteUrl    = $SiteUrl
                            Skipped    = $true
                            SkipReason = $msg
                        } | ConvertTo-Json -Compress)
                    }
                    throw
                }
                finally {
                    try { Disconnect-PnPOnline -ErrorAction SilentlyContinue } catch { }
                }
            }
        return $result
    }
    finally {
        $pool.Close()
        $pool.Dispose()
    }
}
```

### How Invoke-Ingestion.ps1 Discovers and Runs Entities

```powershell
# Invoke-Ingestion.ps1 — shared entry point for all containers

param()

$ErrorActionPreference = 'Stop'
$script:Running = $true

# Load shared modules
Import-Module (Join-Path $PSScriptRoot "modules/KeyVaultHelper.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "modules/StorageHelper.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "modules/WorkerPool.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "modules/LogHelper.psm1") -Force

# Read environment
$tenantKey     = $env:TENANT_KEY
$scheduleTier  = $env:SCHEDULE_TIER        # "core" or "enrichment"
$kvName        = $env:KEYVAULT_NAME
$storageAcct   = $env:STORAGE_ACCOUNT_NAME
$maxParallelism = [int]($env:MAX_PARALLELISM ?? 5)
$runId         = [guid]::NewGuid().ToString()

# Load tenant config
$registry = Get-TenantRegistry -VaultName $kvName
$tenant   = $registry.tenants | Where-Object { $_.tenant_key -eq $tenantKey }
if (-not $tenant) { throw "Tenant '$tenantKey' not found in registry" }
if (-not $tenant.enabled) { Write-Log "Tenant '$tenantKey' is disabled, exiting"; exit 0 }

# Authenticate to tenant
$certPath = Get-CertificateFromKeyVault -VaultName $kvName -CertName $tenant.cert_name
try {
    Connect-ToService -TenantId $tenant.tenant_id -ClientId $tenant.client_id `
                      -CertPath $certPath -Organization $tenant.organization

    # Discover entity modules in ./entities/
    $entityModules = @{}
    Get-ChildItem (Join-Path $PSScriptRoot "entities") -Filter "*.psm1" | ForEach-Object {
        $mod = Import-Module $_.FullName -PassThru -Force
        $config = & "$($mod.Name)\Get-EntityConfig"
        $entityModules[$config.Name] = @{ Module = $mod; Config = $config }
    }

    # Filter to entities this tenant wants for this schedule tier
    $wantedEntities = if ($scheduleTier -eq "core") {
        $tenant.core_entities
    } else {
        $tenant.enrichment_entities
    }
    $toRun = $wantedEntities | Where-Object { $entityModules.ContainsKey($_) }

    Write-Log "Running $($toRun.Count) entities for tenant '$tenantKey' ($scheduleTier): $($toRun -join ', ')"

    # Run each entity
    foreach ($entityName in $toRun) {
        if (-not $script:Running) { break }

        $entity = $entityModules[$entityName]
        $config = $entity.Config
        $date = Get-Date -Format 'yyyy-MM-dd'
        $basePath = "$scheduleTier/$($config.Name)/$tenantKey/$date"

        Write-Log "Starting entity: $entityName"

        # Phase 1: Stream enumeration
        $localFile = Join-Path $env:TEMP "$($config.OutputFile)_${runId}.jsonl"
        $writer = [System.IO.StreamWriter]::new($localFile, $false, [System.Text.Encoding]::UTF8)
        $recordCount = 0
        $entityIds = [System.Collections.Generic.List[string]]::new()

        try {
            & "$($entity.Module.Name)\Invoke-Phase1" `
                -Writer $writer `
                -RecordCount ([ref]$recordCount) `
                -EntityIds $entityIds
        }
        finally {
            $writer.Dispose()
        }

        # Upload Phase 1 file
        Write-ToAdls -StorageAccount $storageAcct `
                     -BlobPath "$basePath/$($config.OutputFile)_${runId}.jsonl" `
                     -LocalFile $localFile
        Remove-Item $localFile -Force

        # Phase 2: Parallel enrichment (if applicable)
        $phase2Count = 0
        $phase2Chunks = 0
        if ($config.Phase2 -and $entityIds.Count -gt 0) {
            $outputDir = Join-Path $env:TEMP $config.DetailType
            New-Item -Path $outputDir -ItemType Directory -Force | Out-Null

            $result = & "$($entity.Module.Name)\Invoke-Phase2" `
                -EntityIds $entityIds.ToArray() `
                -OutputDirectory $outputDir `
                -RunId $runId `
                -PoolSize ([math]::Min($maxParallelism, $tenant.max_parallelism))

            $phase2Count = $result.RecordCount
            $phase2Chunks = $result.ChunkCount

            # Upload Phase 2 chunks
            Get-ChildItem $outputDir -Filter "*.jsonl" | ForEach-Object {
                Write-ToAdls -StorageAccount $storageAcct `
                             -BlobPath "$basePath/$($config.DetailType)/$($_.Name)" `
                             -LocalFile $_.FullName
            }
            Remove-Item $outputDir -Recurse -Force
        }

        # Write manifest
        $manifest = @{
            run_id              = $runId
            tenant_key          = $tenantKey
            tenant_id           = $tenant.tenant_id
            entity_type         = $config.Name
            schedule_tier       = $scheduleTier
            phase1_record_count = $recordCount
            phase2_record_count = $phase2Count
            phase2_chunk_count  = $phase2Chunks
            started_at          = $entityStartTime
            completed_at        = (Get-Date -Format 'o')
            status              = "success"
            errors              = @()
        }
        $manifestJson = $manifest | ConvertTo-Json -Depth 3
        $manifestPath = Join-Path $env:TEMP "_manifest_${runId}.json"
        [System.IO.File]::WriteAllText($manifestPath, $manifestJson)
        Write-ToAdls -StorageAccount $storageAcct `
                     -BlobPath "$basePath/_manifest_${runId}.json" `
                     -LocalFile $manifestPath
        Remove-Item $manifestPath -Force

        Write-Log "Completed entity: $entityName ($recordCount Phase1, $phase2Count Phase2)"
    }
}
finally {
    # Certificate cleanup — overwrite with zeros then delete
    if ($certPath -and (Test-Path $certPath)) {
        $bytes = [byte[]]::new((Get-Item $certPath).Length)
        [System.IO.File]::WriteAllBytes($certPath, $bytes)
        Remove-Item $certPath -Force
    }
}
```

---

## 7. ADLS Landing Zone Structure

### Path Convention

```
{container}/
  {schedule_tier}/
    {entity_type}/
      {tenant_key}/
        {date}/
          {entity}_{run_id}.jsonl                    ← Phase 1 data
          {detail_type}/
            chunk-000_{run_id}.jsonl                 ← Phase 2 chunks
            chunk-001_{run_id}.jsonl
            ...
          _manifest_{run_id}.json                    ← Completion manifest
```

### Example Paths

```
bronze/
  core/
    entra_users/
      contoso/
        2026-02-28/
          entra_users_a1b2c3d4.jsonl
          _manifest_a1b2c3d4.json
      fabrikam/
        2026-02-28/
          entra_users_e5f6g7h8.jsonl
          _manifest_e5f6g7h8.json
    exo_mailboxes/
      contoso/
        2026-02-28/
          exo_mailboxes_a1b2c3d4.jsonl
          _manifest_a1b2c3d4.json

  enrichment/
    exo_mailbox_statistics/
      contoso/
        2026-02-28/
          exo_mailboxes_i9j0k1l2.jsonl              ← Phase 1 re-enumeration
          mailbox_statistics/
            chunk-000_i9j0k1l2.jsonl                ← Phase 2 parallel stats
            chunk-001_i9j0k1l2.jsonl
            chunk-002_i9j0k1l2.jsonl
          _manifest_i9j0k1l2.json
    spo_site_details/
      contoso/
        2026-02-28/
          spo_sites_m3n4o5p6.jsonl                  ← Phase 1 site enumeration
          site_details/
            chunk-000_m3n4o5p6.jsonl                ← Phase 2 per-site details
            chunk-001_m3n4o5p6.jsonl
          _manifest_m3n4o5p6.json
```

### JSONL File Format

Each line is one complete JSON object (no wrapping envelope):

```jsonl
{"id":"user-001","userPrincipalName":"alice@contoso.com","displayName":"Alice Smith",...}
{"id":"user-002","userPrincipalName":"bob@contoso.com","displayName":"Bob Jones",...}
```

### Manifest File Schema

```json
{
  "run_id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "tenant_key": "contoso",
  "tenant_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "entity_type": "exo_mailbox_statistics",
  "schedule_tier": "enrichment",
  "phase1_record_count": 50000,
  "phase2_record_count": 49850,
  "phase2_chunk_count": 10,
  "started_at": "2026-02-28T02:00:00Z",
  "completed_at": "2026-02-28T03:45:22Z",
  "duration_seconds": 6322,
  "status": "success",
  "errors": []
}
```

### Run ID for Idempotency

- UUID generated once at job start; all files for a run share the same `run_id`
- If a job is retried (e.g., after a crash), a new `run_id` is generated
- Duplicate data from retries is handled by SCD Type 1 dedup in the Silver layer — the latest record per key wins
- Manifest `status` field allows Bronze pipeline to optionally filter out failed runs

---

## 8. Databricks Bronze Pipeline

### Auto Loader for JSONL

One streaming table per entity type. Auto Loader reads from a **wildcard path** across all tenant folders:

```python
import dlt
from pyspark.sql.functions import col, lit, input_file_name, current_timestamp, regexp_extract

STORAGE_ACCOUNT = spark.conf.get("storage_account_name")
CONTAINER = "bronze"
BASE_PATH = f"abfss://{CONTAINER}@{STORAGE_ACCOUNT}.dfs.core.windows.net"

def create_bronze_table(entity_type: str, schedule_tier: str, source_system: str):
    """Factory function to create a bronze streaming table for any entity type."""

    @dlt.table(
        name=f"bronze_{entity_type}",
        comment=f"Raw {entity_type} data from all tenants",
        table_properties={
            "quality": "bronze",
            "pipelines.autoOptimize.managed": "true",
            "delta.columnMapping.mode": "name",
        },
    )
    @dlt.expect("valid_record", "id IS NOT NULL OR ExchangeGuid IS NOT NULL")
    def bronze_table():
        # Wildcard * reads across all tenant_key folders
        landing_path = f"{BASE_PATH}/{schedule_tier}/{entity_type}/*/""

        return (
            spark.readStream
            .format("cloudFiles")
            .option("cloudFiles.format", "json")
            .option("cloudFiles.schemaLocation", f"{BASE_PATH}/_schemas/{entity_type}")
            .option("cloudFiles.inferColumnTypes", "true")
            .option("cloudFiles.schemaEvolutionMode", "addNewColumns")
            .option("multiLine", "false")  # JSONL: one object per line
            .option("cloudFiles.useIncrementalListing", "auto")
            .load(landing_path)
            .withColumn("_tenant_key",
                regexp_extract(input_file_name(), r"/([^/]+)/\d{4}-\d{2}-\d{2}/", 1))
            .withColumn("_source_file", input_file_name())
            .withColumn("_source_system", lit(source_system))
            .withColumn("_schedule_tier", lit(schedule_tier))
            .withColumn("_dlt_ingested_at", current_timestamp())
        )

    return bronze_table


# --- Core tier entities ---
create_bronze_table("entra_users", "core", "graph_api")
create_bronze_table("entra_groups", "core", "graph_api")
create_bronze_table("entra_devices", "core", "graph_api")
create_bronze_table("exo_mailboxes", "core", "exchange_online")
create_bronze_table("exo_distribution_groups", "core", "exchange_online")
create_bronze_table("exo_unified_groups", "core", "exchange_online")
create_bronze_table("spo_sites", "core", "sharepoint_online")
create_bronze_table("intune_devices", "core", "graph_api")

# --- Enrichment tier entities ---
create_bronze_table("exo_mailbox_statistics", "enrichment", "exchange_online")
create_bronze_table("exo_folder_statistics", "enrichment", "exchange_online")
create_bronze_table("spo_site_details", "enrichment", "sharepoint_online")
create_bronze_table("intune_device_details", "enrichment", "graph_api")
create_bronze_table("intune_autopilot", "enrichment", "graph_api")
```

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **One table per entity, not per tenant** | `bronze_entra_users` contains data from all tenants. Avoids table proliferation as tenants scale. Tenant isolation is via `_tenant_key` column, extracted from the file path. |
| **Wildcard tenant path** | `{entity_type}/*/` lets Auto Loader discover new tenant folders automatically when a new tenant is onboarded. No schema or pipeline changes needed. |
| **JSONL format** | `multiLine: false` — each line is a complete JSON record. Enables streaming writes from containers without buffering. |
| **Schema evolution** | `addNewColumns` mode allows API response changes (new fields) to be absorbed without pipeline failures. |
| **`_tenant_key` from path** | Extracted via regex from the file path. Reliable because the container controls the upload path. |

### Phase 2 Chunk Handling

For enrichment entities with Phase 2 chunks, Auto Loader reads from the `{detail_type}/` subfolder. Create additional bronze tables:

```python
# Phase 2 detail tables (chunks land in subfolder)
create_bronze_table("exo_mailbox_statistics_detail", "enrichment", "exchange_online")
# landing_path override: .../exo_mailbox_statistics/*/YYYY-MM-DD/mailbox_statistics/
```

Alternatively, use a single table with the `recursiveFileLookup` option to read both the Phase 1 file and Phase 2 chunks — depends on whether you want them in the same or separate tables.

---

## 9. Databricks Silver Pipeline

### SCD Type 1 with Tenant-Scoped Keys

Silver tables apply transformations and deduplicate using `dlt.apply_changes()`. The SCD key includes `tenant_key` so records from different tenants never collide:

```python
import dlt
from pyspark.sql.functions import col, lower, trim, concat_ws, when, lit, expr

def create_silver_users():
    @dlt.view(name="silver_users_v")
    def users_cleaned():
        return (
            dlt.read_stream("bronze_entra_users")
            .select(
                concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
                col("_tenant_key").alias("tenant_key"),
                col("id"),
                col("userPrincipalName"),
                lower(trim(col("mail"))).alias("mail"),
                col("displayName").alias("display_name"),
                col("givenName").alias("given_name"),
                col("surname"),
                col("jobTitle").alias("job_title"),
                col("department"),
                col("officeLocation").alias("office_location"),
                col("city"),
                col("state"),
                col("country"),
                col("companyName").alias("company_name"),
                col("accountEnabled").alias("account_enabled"),
                col("onPremisesSyncEnabled").alias("on_premises_sync_enabled"),
                col("onPremisesLastSyncDateTime").alias("on_premises_last_sync"),
                col("createdDateTime").alias("created_at"),
                col("lastSignInDateTime").alias("last_sign_in_at"),
                # License detection
                when(
                    expr("exists(assignedLicenses, x -> x.skuId = '06ebc4ee-1bb5-47dd-8120-11324bc54e06')"),
                    lit("E5")
                ).when(
                    expr("exists(assignedLicenses, x -> x.skuId = '05e9a617-0261-4cee-bb44-138d3ef5d965')"),
                    lit("E3")
                ).when(
                    expr("exists(assignedLicenses, x -> x.skuId = '66b55226-6b4f-492c-910c-a3b7a3c9d993')"),
                    lit("F3")
                ).when(
                    expr("exists(assignedLicenses, x -> x.skuId = '18181a46-0d4e-45cd-891e-60aabd171b4e')"),
                    lit("E1")
                ).otherwise(lit(None)).alias("license_type"),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )

    dlt.create_streaming_table(
        name="silver_users",
        comment="Cleaned and deduplicated Entra users across all tenants",
        table_properties={"quality": "silver"},
    )

    dlt.apply_changes(
        target="silver_users",
        source="silver_users_v",
        keys=["_scd_key"],                      # tenant_key + id
        sequence_by=col("_dlt_ingested_at"),    # latest record wins
        stored_as_scd_type=1,
    )


def create_silver_groups():
    @dlt.view(name="silver_groups_v")
    def groups_cleaned():
        return (
            dlt.read_stream("bronze_entra_groups")
            .select(
                concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
                col("_tenant_key").alias("tenant_key"),
                col("id"),
                col("displayName").alias("display_name"),
                lower(trim(col("mail"))).alias("mail"),
                col("mailEnabled").alias("mail_enabled"),
                col("securityEnabled").alias("security_enabled"),
                # Group type classification
                when(
                    expr("array_contains(groupTypes, 'Unified')"), lit("Microsoft365")
                ).when(
                    col("mailEnabled") & col("securityEnabled"), lit("MailEnabledSecurity")
                ).when(
                    col("mailEnabled") & ~col("securityEnabled"), lit("Distribution")
                ).when(
                    expr("array_contains(groupTypes, 'DynamicMembership')"), lit("Dynamic")
                ).otherwise(lit("Security")).alias("group_type"),
                col("membershipRule").alias("membership_rule"),
                col("createdDateTime").alias("created_at"),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )

    dlt.create_streaming_table(name="silver_groups", table_properties={"quality": "silver"})
    dlt.apply_changes(
        target="silver_groups",
        source="silver_groups_v",
        keys=["_scd_key"],
        sequence_by=col("_dlt_ingested_at"),
        stored_as_scd_type=1,
    )


def create_silver_mailboxes():
    @dlt.view(name="silver_mailboxes_v")
    def mailboxes_cleaned():
        return (
            dlt.read_stream("bronze_exo_mailboxes")
            .select(
                concat_ws("_", col("_tenant_key"), col("ExchangeGuid")).alias("_scd_key"),
                col("_tenant_key").alias("tenant_key"),
                col("ExchangeGuid").alias("exchange_guid"),
                lower(trim(col("PrimarySmtpAddress"))).alias("primary_smtp_address"),
                col("DisplayName").alias("display_name"),
                col("RecipientTypeDetails").alias("recipient_type_details"),
                col("WhenCreated").alias("created_at"),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )

    dlt.create_streaming_table(name="silver_mailboxes", table_properties={"quality": "silver"})
    dlt.apply_changes(
        target="silver_mailboxes",
        source="silver_mailboxes_v",
        keys=["_scd_key"],
        sequence_by=col("_dlt_ingested_at"),
        stored_as_scd_type=1,
    )


def create_silver_mailbox_statistics():
    @dlt.view(name="silver_mailbox_statistics_v")
    def stats_cleaned():
        return (
            dlt.read_stream("bronze_exo_mailbox_statistics")
            .select(
                concat_ws("_", col("_tenant_key"), col("MailboxGuid")).alias("_scd_key"),
                col("_tenant_key").alias("tenant_key"),
                col("MailboxGuid").alias("mailbox_guid"),
                col("DisplayName").alias("display_name"),
                col("ItemCount").cast("long").alias("item_count"),
                # Convert "123.4 MB (129,345,536 bytes)" → numeric MB
                regexp_extract(col("TotalItemSize"), r"\(([\d,]+)\s+bytes\)", 1)
                    .cast("long").alias("total_item_size_bytes"),
                (col("total_item_size_bytes") / 1048576).alias("total_item_size_mb"),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )

    dlt.create_streaming_table(name="silver_mailbox_statistics", table_properties={"quality": "silver"})
    dlt.apply_changes(
        target="silver_mailbox_statistics",
        source="silver_mailbox_statistics_v",
        keys=["_scd_key"],
        sequence_by=col("_dlt_ingested_at"),
        stored_as_scd_type=1,
    )


def create_silver_spo_sites():
    @dlt.view(name="silver_spo_sites_v")
    def sites_cleaned():
        return (
            dlt.read_stream("bronze_spo_sites")
            .select(
                concat_ws("_", col("_tenant_key"), col("SiteId")).alias("_scd_key"),
                col("_tenant_key").alias("tenant_key"),
                col("SiteId").alias("site_id"),
                col("Url").alias("url"),
                col("Title").alias("title"),
                col("Template").alias("template"),
                col("StorageUsageCurrent").cast("long").alias("storage_usage_mb"),
                col("StorageMaximumLevel").cast("long").alias("storage_quota_mb"),
                col("Owner").alias("owner"),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )

    dlt.create_streaming_table(name="silver_spo_sites", table_properties={"quality": "silver"})
    dlt.apply_changes(
        target="silver_spo_sites",
        source="silver_spo_sites_v",
        keys=["_scd_key"],
        sequence_by=col("_dlt_ingested_at"),
        stored_as_scd_type=1,
    )


def create_silver_devices():
    @dlt.view(name="silver_devices_v")
    def devices_cleaned():
        return (
            dlt.read_stream("bronze_entra_devices")
            .select(
                concat_ws("_", col("_tenant_key"), col("deviceId")).alias("_scd_key"),
                col("_tenant_key").alias("tenant_key"),
                col("deviceId").alias("device_id"),
                col("displayName").alias("display_name"),
                col("operatingSystem").alias("operating_system"),
                col("operatingSystemVersion").alias("os_version"),
                col("trustType").alias("trust_type"),
                col("isManaged").alias("is_managed"),
                col("isCompliant").alias("is_compliant"),
                col("approximateLastSignInDateTime").alias("last_sign_in_at"),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )

    dlt.create_streaming_table(name="silver_devices", table_properties={"quality": "silver"})
    dlt.apply_changes(
        target="silver_devices",
        source="silver_devices_v",
        keys=["_scd_key"],
        sequence_by=col("_dlt_ingested_at"),
        stored_as_scd_type=1,
    )


def create_silver_intune_devices():
    @dlt.view(name="silver_intune_devices_v")
    def intune_cleaned():
        return (
            dlt.read_stream("bronze_intune_devices")
            .select(
                concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
                col("_tenant_key").alias("tenant_key"),
                col("id"),
                col("deviceName").alias("device_name"),
                col("azureADDeviceId").alias("azure_ad_device_id"),
                col("operatingSystem").alias("operating_system"),
                col("osVersion").alias("os_version"),
                col("complianceState").alias("compliance_state"),
                col("managementAgent").alias("management_agent"),
                col("enrolledDateTime").alias("enrolled_at"),
                col("lastSyncDateTime").alias("last_sync_at"),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )

    dlt.create_streaming_table(name="silver_intune_devices", table_properties={"quality": "silver"})
    dlt.apply_changes(
        target="silver_intune_devices",
        source="silver_intune_devices_v",
        keys=["_scd_key"],
        sequence_by=col("_dlt_ingested_at"),
        stored_as_scd_type=1,
    )


# Invoke all silver table creators
create_silver_users()
create_silver_groups()
create_silver_mailboxes()
create_silver_mailbox_statistics()
create_silver_spo_sites()
create_silver_devices()
create_silver_intune_devices()
```

### Silver Table Catalog

| Silver Table | Bronze Source | SCD Key | Key Transformations |
|-------------|-------------|---------|---------------------|
| `silver_users` | `bronze_entra_users` | `{tenant_key}_{id}` | Normalize emails lowercase, extract license type from `assignedLicenses` array |
| `silver_groups` | `bronze_entra_groups` | `{tenant_key}_{id}` | Classify `group_type`: Security, Distribution, MailEnabledSecurity, Microsoft365, Dynamic |
| `silver_devices` | `bronze_entra_devices` | `{tenant_key}_{deviceId}` | Flatten `registeredOwners`/`registeredUsers`, map OS types |
| `silver_mailboxes` | `bronze_exo_mailboxes` | `{tenant_key}_{ExchangeGuid}` | Normalize email addresses, map recipient type details |
| `silver_mailbox_statistics` | `bronze_exo_mailbox_statistics` | `{tenant_key}_{MailboxGuid}` | Convert `ByteQuantifiedSize` strings to MB |
| `silver_distribution_groups` | `bronze_exo_distribution_groups` | `{tenant_key}_{ExchangeGuid}` | Classify managed/unmanaged |
| `silver_unified_groups` | `bronze_exo_unified_groups` | `{tenant_key}_{ExchangeGuid}` | Map group access type |
| `silver_spo_sites` | `bronze_spo_sites` | `{tenant_key}_{SiteId}` | Normalize URLs, classify site template types |
| `silver_spo_site_details` | `bronze_spo_site_details` | `{tenant_key}_{hash(SiteUrl)}` | Flatten storage metrics, aggregate doc library counts |
| `silver_intune_devices` | `bronze_intune_devices` | `{tenant_key}_{id}` | Map compliance state, OS version parsing |
| `silver_intune_autopilot` | `bronze_intune_autopilot` | `{tenant_key}_{id}` | Map deployment profile status |

### No Mapping Tables

Identity mapping (cross-tenant user/group matching) is **out of scope** for this build. Silver tables contain entity data only. Mapping is deferred to a future phase that will consume Silver tables as input.

---

## 10. Orchestration (ADF)

### Master Pipeline — Core (Daily)

```
pl_orchestrate_core
  Parameters:
    - keyVaultName: string
    - storageAccountName: string
    - graphImage: string (ACR image tag)
    - exoImage: string (ACR image tag)
    - spoImage: string (ACR image tag)
    - dltPipelineId: string (Databricks pipeline ID)
    - databricksWorkspaceUrl: string

  Activities:
    1. Lookup_Tenant_Registry (Web Activity)
       → GET Key Vault secret "tenant-registry"
       → Parse JSON, filter: enabled == true AND core_schedule matches

    2. ForEach_Tenant (ForEach, parallel)
       Items: @activity('Lookup_Tenant_Registry').output.tenants
       IsSequential: false

       Activities inside ForEach:
         2a. If_Needs_Graph (IfCondition)
             Condition: tenant has core_entities containing entra_* or intune_*
             True → Execute pl_run_container_job
                     Parameters:
                       containerImage: @pipeline().parameters.graphImage
                       tenantKey: @item().tenant_key
                       scheduleTier: "core"
                       keyVaultName: @pipeline().parameters.keyVaultName
                       storageAccountName: @pipeline().parameters.storageAccountName
                       maxParallelism: @item().max_parallelism

         2b. If_Needs_EXO (IfCondition)
             Condition: tenant has core_entities containing exo_*
             True → Execute pl_run_container_job
                     Parameters:
                       containerImage: @pipeline().parameters.exoImage
                       tenantKey: @item().tenant_key
                       scheduleTier: "core"
                       ...

         2c. If_Needs_SPO (IfCondition)
             Condition: tenant has core_entities containing spo_*
             True → Execute pl_run_container_job
                     Parameters:
                       containerImage: @pipeline().parameters.spoImage
                       tenantKey: @item().tenant_key
                       scheduleTier: "core"
                       ...

    3. Run_DLT (Execute Pipeline, after ForEach completes)
       → pl_run_dlt(dltPipelineId, databricksWorkspaceUrl)
```

### Master Pipeline — Enrichment (Weekly)

Same structure as core, but:
- Trigger: weekly (e.g. Sunday 2:00 AM)
- Filters tenants where `enrichment_schedule` matches
- Uses `enrichment_entities` list
- `scheduleTier: "enrichment"`
- Enrichment containers use larger ACA job specs (more CPU/memory for RunspacePool)

### Child Pipeline — Run Container Job (Fire-and-Poll)

```
pl_run_container_job
  Parameters:
    - containerImage: string
    - tenantKey: string
    - scheduleTier: string
    - keyVaultName: string
    - storageAccountName: string
    - maxParallelism: int

  Activities:
    1. Start_Job (Web Activity)
       Method: POST
       URL: https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/
            providers/Microsoft.App/jobs/{job}/start?api-version=2024-03-01
       Body:
         {
           "template": {
             "containers": [{
               "image": "@{pipeline().parameters.containerImage}",
               "name": "ingest",
               "env": [
                 { "name": "TENANT_KEY",           "value": "@{pipeline().parameters.tenantKey}" },
                 { "name": "SCHEDULE_TIER",         "value": "@{pipeline().parameters.scheduleTier}" },
                 { "name": "KEYVAULT_NAME",         "value": "@{pipeline().parameters.keyVaultName}" },
                 { "name": "STORAGE_ACCOUNT_NAME",  "value": "@{pipeline().parameters.storageAccountName}" },
                 { "name": "MAX_PARALLELISM",       "value": "@{pipeline().parameters.maxParallelism}" }
               ],
               "resources": {
                 "cpu": "@{if(equals(pipeline().parameters.scheduleTier,'core'), 1, 4)}",
                 "memory": "@{if(equals(pipeline().parameters.scheduleTier,'core'), '2Gi', '8Gi')}"
               }
             }]
           }
         }
       Authentication: Managed Identity (system-assigned)

    2. Get_Execution_Name (Set Variable)
       Value: @activity('Start_Job').output.id  → extract execution name

    3. Wait_For_Job (Until Loop)
       Condition: @or(
         equals(variables('jobStatus'), 'Succeeded'),
         equals(variables('jobStatus'), 'Failed')
       )
       Timeout: 4 hours (core), 8 hours (enrichment)

       Activities inside loop:
         3a. Wait_30s (Wait Activity, 30 seconds)
         3b. Check_Status (Web Activity)
             GET .../executions/{executionName}?api-version=2024-03-01
         3c. Set_Status (Set Variable)
             Value: @activity('Check_Status').output.properties.status

    4. Check_Success (IfCondition)
       Condition: @equals(variables('jobStatus'), 'Succeeded')
       False → Fail pipeline with error details
```

### Child Pipeline — Run DLT

```
pl_run_dlt
  Parameters:
    - dltPipelineId: string
    - databricksWorkspaceUrl: string

  Activities:
    1. Start_DLT_Update (Web Activity)
       Method: POST
       URL: @{pipeline().parameters.databricksWorkspaceUrl}/api/2.0/pipelines/
            @{pipeline().parameters.dltPipelineId}/updates
       Body: { "full_refresh": false }
       Authentication: MSI against AAD resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d

    2. Wait_For_DLT (Until Loop)
       Poll every 60s, timeout 2 hours
       Check: GET .../pipelines/{id}/updates/{updateId}
       Terminal states: COMPLETED, FAILED, CANCELED

    3. Check_DLT_Success (IfCondition)
       Condition: state == COMPLETED
       False → Fail with event log details
```

### Parameterization (vs Hardcoded)

All infrastructure references are pipeline parameters or linked service properties — never hardcoded:

| Current (antipattern) | New (parameterized) |
|----------------------|---------------------|
| Subscription ID in URL | `@pipeline().parameters.subscriptionId` or linked service |
| Resource group in URL | `@pipeline().parameters.resourceGroupName` or linked service |
| ACR image tag `v35` | `@pipeline().parameters.graphImage` |
| DLT pipeline GUIDs | `@pipeline().parameters.dltPipelineId` |
| Databricks workspace URL | `@pipeline().parameters.databricksWorkspaceUrl` |

---

## 11. Infrastructure Guidance

### ACA Job Sizing

| Tier | CPU | Memory | Replica Timeout | Use Case |
|------|-----|--------|----------------|----------|
| Core | 1 | 2Gi | 3600s (1hr) | Serial streaming only, low memory footprint |
| Enrichment | 2–4 | 4–8Gi | 14400s (4hr) | RunspacePool with 5–10 parallel workers |

Scale `max_parallelism` per tenant based on API throttle limits. Graph API allows ~10,000 requests/10min. EXO allows ~10,000 requests/10min per tenant. PnP/SPO is more restrictive (~1,000/min).

### Terraform Module Structure

```hcl
# modules/container_app_job/main.tf
resource "azurerm_container_app_job" "this" {
  name                         = var.name
  resource_group_name          = var.resource_group_name
  location                     = var.location
  container_app_environment_id = var.container_app_environment_id
  replica_timeout_in_seconds   = var.replica_timeout

  template {
    container {
      name   = "ingest"
      image  = var.image
      cpu    = var.cpu
      memory = var.memory

      # Env vars set at runtime by ADF, not in Terraform
    }
  }

  identity {
    type = "SystemAssigned"
  }

  # Manual trigger type — ADF starts jobs via REST API
  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }
}
```

### Key Vault Configuration

```hcl
# Per-tenant certificates
resource "azurerm_key_vault_certificate" "tenant_certs" {
  for_each     = { for t in var.tenants : t.tenant_key => t }
  name         = each.value.cert_name
  key_vault_id = azurerm_key_vault.this.id

  certificate {
    contents = filebase64(each.value.cert_pfx_path)
  }
}

# Tenant registry secret
resource "azurerm_key_vault_secret" "tenant_registry" {
  name         = "tenant-registry"
  value        = jsonencode({ tenants = var.tenants })
  key_vault_id = azurerm_key_vault.this.id
}
```

### RBAC Assignments

| Principal | Resource | Role |
|-----------|----------|------|
| ACA Job (system identity) | Key Vault | Key Vault Secrets User, Key Vault Certificate User |
| ACA Job (system identity) | ADLS Storage Account | Storage Blob Data Contributor |
| ADF (system identity) | ACA Job | Contributor (to start jobs via ARM) |
| ADF (system identity) | Key Vault | Key Vault Secrets User (to read tenant registry) |
| ADF (system identity) | Databricks Workspace | Contributor |
| Databricks Access Connector | ADLS Storage Account | Storage Blob Data Reader |

### Network Considerations

- ACA environment with VNet integration for private endpoint access to Key Vault and Storage
- Databricks workspace in same VNet or with private link
- ADF managed VNet with private endpoints to ACA, Key Vault, Databricks
- No public endpoints required

---

## 12. Repo Structure

```
src/analytics/
  containers/
    graph-ingest/
      Dockerfile
      scripts/
        Invoke-Ingestion.ps1              # Shared entry point
        entities/
          EntraUsers.psm1
          EntraGroups.psm1
          EntraDevices.psm1
          IntuneDevices.psm1
          IntuneDeviceDetails.psm1        # enrichment, Phase 2
          IntuneAutopilot.psm1            # enrichment, serial
        modules/
          KeyVaultHelper.psm1
          StorageHelper.psm1              # Az.Storage variant
          WorkerPool.psm1
          RetryHelper.psm1
          LogHelper.psm1

    exo-ingest/
      Dockerfile
      scripts/
        Invoke-Ingestion.ps1              # Same structure, different entities
        entities/
          ExoMailboxes.psm1
          ExoDistributionGroups.psm1
          ExoUnifiedGroups.psm1
          ExoMailboxStatistics.psm1       # enrichment, Phase 2
          ExoFolderStatistics.psm1        # enrichment, Phase 2
        modules/
          KeyVaultHelper.psm1
          StorageHelperRest.psm1          # REST API variant (no Az.Storage)
          WorkerPool.psm1
          RetryHelper.psm1
          LogHelper.psm1

    spo-ingest/
      Dockerfile
      scripts/
        Invoke-Ingestion.ps1
        entities/
          SpoSites.psm1
          SpoSiteDetails.psm1            # enrichment, Phase 2
        modules/
          KeyVaultHelper.psm1
          StorageHelper.psm1
          WorkerPool.psm1
          RetryHelper.psm1
          LogHelper.psm1

  databricks/
    dlt/
      pl_bronze.py                        # Auto Loader streaming tables
      pl_silver.py                        # SCD Type 1 transformations
    unity-catalog/
      setup.py                            # Catalog, schemas, storage creds

  datafactory/
    pipelines/
      pl_orchestrate_core.json
      pl_orchestrate_enrichment.json
      pl_run_container_job.json
      pl_run_dlt.json
    triggers/
      tr_daily_core.json
      tr_weekly_enrichment.json

infra/analytics/
  modules/
    container_app_environment/
    container_app_job/
    resource_group/
    key_vault/
    storage_account/
    databricks_workspace/
    databricks_access_connector/
    data_factory/
    private_endpoint/
  environments/
    dev/
      main.tf
      variables.tf
      terraform.tfvars
    prod/
      main.tf
      variables.tf
      terraform.tfvars
```

### Shared Module Strategy

`KeyVaultHelper.psm1`, `WorkerPool.psm1`, `RetryHelper.psm1`, and `LogHelper.psm1` are **identical** across all three containers. `StorageHelper.psm1` has two variants:

- **Az.Storage variant** (graph + spo): Uses `Set-AzStorageBlobContent` with Managed Identity
- **REST API variant** (exo): Uses direct REST calls to `https://{account}.dfs.core.windows.net/` with Managed Identity token from `$env:IDENTITY_ENDPOINT`

Options for keeping shared modules in sync:
1. **Copy at build time** — Dockerfile copies from a shared `modules/` directory
2. **Git submodule** — shared modules in a separate repo
3. **Build script** — CI validates all copies are identical

---

## 13. Implementation Sequence

### Phase A — Foundation (Week 1–2)

1. **Set up repo structure** — Create folder layout per §12
2. **Implement shared modules** — `KeyVaultHelper`, `StorageHelper` (both variants), `LogHelper`, `RetryHelper`
3. **Implement `WorkerPool.psm1`** — RunspacePool creation, round-robin partitioning, dispatch/polling
4. **Implement `Invoke-Ingestion.ps1`** — Entry point with tenant registry loading, entity discovery, Phase 1/2 orchestration, manifest writing
5. **Build Dockerfiles** — All three containers with correct module sets

### Phase B — Core Entities (Week 2–3)

6. **Implement core entity modules** — Start with `EntraUsers.psm1` as the template, then:
   - `EntraGroups.psm1`, `EntraDevices.psm1`
   - `ExoMailboxes.psm1`, `ExoDistributionGroups.psm1`, `ExoUnifiedGroups.psm1`
   - `SpoSites.psm1`
   - `IntuneDevices.psm1`
7. **Test locally** — Run containers against a dev tenant, verify JSONL output and ADLS uploads

### Phase C — Enrichment Entities (Week 3–4)

8. **Implement enrichment entity modules** — These all have Phase 2:
   - `ExoMailboxStatistics.psm1` (RunspacePool with EXO pre-auth)
   - `ExoFolderStatistics.psm1` (same pattern, expensive — opt-in)
   - `SpoSiteDetails.psm1` (RunspacePool with PnP per-site connect)
   - `IntuneDeviceDetails.psm1` (RunspacePool with Graph)
   - `IntuneAutopilot.psm1` (serial, no Phase 2)
9. **Test Phase 2** — Verify RunspacePool scaling, chunk file output, retry behavior

### Phase D — Databricks (Week 4–5)

10. **Implement `pl_bronze.py`** — Auto Loader tables for all entities with wildcard tenant paths
11. **Implement `pl_silver.py`** — SCD Type 1 transformations for all entities
12. **Test end-to-end** — Container output → Bronze → Silver with multi-tenant data

### Phase E — Orchestration (Week 5–6)

13. **Implement ADF pipelines** — `pl_run_container_job`, `pl_run_dlt`, `pl_orchestrate_core`, `pl_orchestrate_enrichment`
14. **Implement triggers** — Daily core, weekly enrichment
15. **Test orchestration** — Full end-to-end with 2+ tenants

### Phase F — Infrastructure (Week 6–7)

16. **Implement Terraform** — Modules for all resources, dev environment
17. **CI/CD** — Container image builds, Terraform apply, DLT deployment
18. **Monitoring** — ADF alerts, DLT event log queries, manifest-based completeness checks

---

## 14. What to Keep vs Replace

### Patterns to Carry Forward

| Pattern | Where It's Used | Why Keep It |
|---------|----------------|-------------|
| Certificate-based M365 auth via Key Vault | `KeyVaultHelper.psm1` | Proven, secure, no secrets in config |
| Managed Identity for Azure resources | ACA → KV, ACA → ADLS | Zero credential management |
| Auto Loader for Bronze ingestion | `pl_bronze.py` | Incremental, schema evolution, exactly-once |
| SCD Type 1 via `dlt.apply_changes()` | `pl_silver.py` | Correct dedup semantics, handles late arrivals |
| ADF fire-and-poll for ACA Jobs | `pl_run_container_job` | Reliable, handles long-running jobs |
| ADF fire-and-poll for DLT | `pl_run_dlt` | MSI auth to Databricks, proper status tracking |
| Separate containers for Graph/EXO/SPO | Dockerfiles | Solves OData assembly conflicts |
| Certificate cleanup (zero + delete) | `finally` blocks | Security best practice |
| DLT table properties (autoOptimize, columnMapping) | Bronze/Silver tables | Performance and compatibility |

### Antipatterns to Replace

| Antipattern | Current Code | Replacement |
|-------------|-------------|-------------|
| `$array += $item` (O(n²)) | `GraphHelper.psm1` lines 171, 248, 289, etc. | `[System.Collections.Generic.List[object]]` |
| Entire dataset buffered in memory | All entity functions | `StreamWriter` with JSONL line-by-line writes |
| Single JSON file with wrapping envelope | `Write-ToAdls` metadata wrapper | JSONL files + separate manifest file |
| N+1 API calls (OneDrive per user) | `Get-GraphOneDriveSites` | Batch Graph API or `$batch` endpoint |
| No retry/throttle handling for Graph | All Graph functions | `Invoke-WithRetry` with exponential backoff |
| Sequential ADF ForEach | `isSequential: true` | Parallel ForEach across tenants and containers |
| Hardcoded ARM URLs and GUIDs | Pipeline JSON | Parameterized pipeline variables |
| One entity per ACA job invocation | ADF dispatches per entity | Container processes all its entities internally |
| `source`/`target` environment model | ADLS paths, Bronze tables | `tenant_key` column, wildcard paths |
| One table per entity+environment | `entra_users_source` + `entra_users_target` | Single `entra_users` table with `tenant_key` column |
| Timestamp-based filenames | `{entity}_{HHmmss}.json` | `{entity}_{run_id}.jsonl` with UUID |
| Duplicate function implementations | `Get-ExoMailboxes` in both GraphHelper and ExoIngestion | Single implementation per entity module |

---

## Appendix: License SKU IDs

Used in Silver `users` table for license type detection:

| License | SKU ID |
|---------|--------|
| Microsoft 365 E5 | `06ebc4ee-1bb5-47dd-8120-11324bc54e06` |
| Microsoft 365 E3 | `05e9a617-0261-4cee-bb44-138d3ef5d965` |
| Microsoft 365 F3 | `66b55226-6b4f-492c-910c-a3b7a3c9d993` |
| Microsoft 365 E1 | `18181a46-0d4e-45cd-891e-60aabd171b4e` |

## Appendix: EXO REST Upload (No Az.Storage)

The EXO container cannot use `Az.Storage` because it pulls in OData assemblies that conflict with `ExchangeOnlineManagement`. Instead, it uploads to ADLS Gen2 via raw REST API:

```powershell
function Write-ToAdlsRest {
    param(
        [string]$StorageAccountName,
        [string]$ContainerName,
        [string]$BlobPath,
        [string]$LocalFile
    )

    # Get Managed Identity token from ACA identity endpoint
    $tokenResponse = Invoke-RestMethod -Uri "$($env:IDENTITY_ENDPOINT)?resource=https://storage.azure.com/&api-version=2019-08-01" `
        -Headers @{ "X-IDENTITY-HEADER" = $env:IDENTITY_HEADER }
    $token = $tokenResponse.access_token
    $headers = @{
        "Authorization" = "Bearer $token"
        "x-ms-version"  = "2021-08-06"
    }

    $baseUrl = "https://${StorageAccountName}.dfs.core.windows.net/${ContainerName}/${BlobPath}"
    $content = [System.IO.File]::ReadAllBytes($LocalFile)

    # 1. Create file
    Invoke-RestMethod -Uri "${baseUrl}?resource=file" `
        -Method PUT -Headers $headers

    # 2. Append data
    Invoke-RestMethod -Uri "${baseUrl}?action=append&position=0" `
        -Method PATCH -Headers $headers `
        -Body $content -ContentType "application/octet-stream"

    # 3. Flush (finalize)
    Invoke-RestMethod -Uri "${baseUrl}?action=flush&position=$($content.Length)" `
        -Method PATCH -Headers $headers
}
```

## Appendix: PnP Certificate Handling

PnP.PowerShell uses `-CertificateBase64Encoded` instead of a file path or thumbprint:

```powershell
# Export PFX bytes from Key Vault certificate, then Base64 encode
$cert = Get-AzKeyVaultCertificate -VaultName $vaultName -Name $certName
$secret = Get-AzKeyVaultSecret -VaultName $vaultName -Name $cert.Name -AsPlainText
$certBytes = [System.Convert]::FromBase64String($secret)
$x509 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certBytes, [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
)
$pfxBytes = $x509.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx
)
$certBase64 = [System.Convert]::ToBase64String($pfxBytes)

# Use with PnP
Connect-PnPOnline -Url $adminUrl -ClientId $clientId `
    -Tenant $tenantDomain -CertificateBase64Encoded $certBase64
```
