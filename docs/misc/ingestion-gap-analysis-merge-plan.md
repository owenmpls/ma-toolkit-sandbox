# Ingestion System Comparison: Landing-Focused Gap Analysis & Merge Plan

> **Scope:** Ingestion to landing only. DLT pipelines, Databricks schema, and Dataverse export are out of scope except as backward-compatibility constraints on the landing data structure.
>
> **Repos compared:**
> - **Main repo** (`ma-toolkit`) -- ADF-orchestrated ingestion with 2 containers, ~16 entity types
> - **Sandbox repo** (`ma-toolkit-sandbox`) -- Azure Functions-orchestrated ingestion with 3 containers, 39 entity types

---

## 1. Architecture Overview

| Dimension | Main Repo (ma-toolkit) | Sandbox (ma-toolkit-sandbox) |
|-----------|----------------------|------------------------------|
| **Orchestration** | Azure Data Factory (JSON pipelines) | Azure Functions (C# .NET 8, isolated worker) |
| **Containers** | 2 (graph-ingest, exo-ingest) | 3 (graph-ingest, exo-ingest, spo-ingest) |
| **Entity count** | ~16 types (flat list) | 39 types across 3 tiers |
| **Landing format** | JSON wrapper + nested `data` array | JSONL (flat records) + separate manifests |
| **Landing path** | `{tenant_name}/{entity}/{date}/` | `{entity}/{tenant_key}/{date}/` |
| **Entity naming** | Short names in ADF (`users`, `mailboxes`) | Fully-qualified (`entra_users`, `exo_mailboxes`) |
| **Entity discovery** | Hardcoded lists in ADF ForEach loops | Entity registry with tier-based algebraic selection |
| **Scheduling** | ADF triggers (every 6h, uniform) | Cron expressions (6h / daily / weekly by tier) |
| **Multi-entity per run** | One ACA job per entity per tenant | Multiple entities per container execution |
| **Phase 2 enrichment** | Not present | Built-in with parallel worker pool |
| **Overlap protection** | None (relies on ADF concurrency settings) | Blob-based tracking with 8hr timeout + force override |
| **Manual trigger** | ADF portal only | HTTP POST `/api/run` with preview endpoint |
| **Run observability** | ADF activity logs + status blobs | JSONL run/task history blobs (DLT-queryable) |
| **Tests** | None for orchestration | 15 unit tests (EntityResolver, TenantResolver) |

---

## 2. Orchestration Comparison

### Main Repo: ADF Pipelines

The orchestration layer is a set of declarative ADF pipeline JSONs:

- **`pl_orchestrate_tenant_ingestion`** -- Primary entry point. Accepts `tenantName`, looks up config from `tenant_list.json` in ADLS, runs a ForEach loop per container type (Graph entities, EXO entities), writes success/failure status blobs.
- **`pl_orchestrate_m365_ingestion`** -- Alternate entry point with hardcoded source/target entity lists (4 parallel ForEach loops). Also triggers DLT after ingestion.
- **`pl_ingest_entity`** -- Child pipeline: starts one ACA job via ARM API, polls execution status every 30s until completion, fails pipeline if job status != "Succeeded".
- **Triggers**: Per-tenant schedule triggers every 6 hours, staggered by 1 hour.

**Strengths:** Simple, visual, Azure-native monitoring.

**Weaknesses:** Entity lists are hardcoded in ForEach arrays or in `tenant_list.json` (mixing entity config with tenant config). No overlap protection. No manual run API. No programmatic entity selection. Pipeline JSON is hard to review/test. Adding new entity types requires editing multiple pipeline JSONs.

### Sandbox: Azure Functions Orchestrator

A C# Azure Functions app with 3 endpoints and a timer function:

- **`IngestionTimerFunction`** -- Runs every minute, evaluates cron schedules against job definitions, checks for active run overlap, dispatches new runs.
- **`ManualRunFunction`** -- HTTP POST `/api/run`: accepts job name or ad-hoc entity/tier selection, supports tenant overrides, force flag to bypass overlap protection. Returns 409 on overlap.
- **`PreviewFunction`** -- HTTP POST `/api/preview`: dry-run that shows resolved tenants, entities, container groups, and task count without executing.

**Core services:**

- **ConfigLoader**: Validates 4 JSON config files at startup (entity registry, tenants, jobs, storage) with strict cross-referencing (tier range, container references, tenant uniqueness).
- **EntityResolver**: Algebraic selection -- `(include_tiers) U (include_entities) - (exclude_entities)`. Returns entities in registry order.
- **TenantResolver**: Three modes -- `all` (enabled only), `specific` (by key), `all_except` (exclusion list). Always filters disabled tenants.
- **ContainerJobDispatcher**: ARM API calls to start ACA jobs, passes all config as env vars.
- **RunTracker**: Blob-based state tracking with 8-hour timeout. Polls ACA status on timer ticks. Finalizes to JSONL history blobs.

**Strengths:** Testable, extensible, operationally transparent. Tier system enables graduated scheduling. Manual run + preview endpoints. Overlap protection. Full run history.

**Weaknesses:** More infrastructure to deploy (Azure Functions app). Newer/less battle-tested.

---

## 3. Container Comparison

### Main Repo Containers

Two containers in `src/analytics/containers/analytics-ingest/`:

| Container | Image | Entities | Pattern |
|-----------|-------|----------|---------|
| graph-ingest | `graph-ingest:v46` | users, contacts, groups, group_members, onedrive, sharepoint, group_owners, devices | Single entity per execution |
| exo-ingest | `exo-ingest:v46` (pinned ACR base) | mailboxes, mail_users, mail_contacts | Single entity per execution |

- **Scripts**: `Invoke-GraphIngestion.ps1`, `Invoke-ExoIngestion.ps1` -- monolithic scripts per source type.
- **Helper modules**: `GraphHelper.psm1`, `ExoHelper.psm1`, `KeyVaultHelper.psm1`, `StorageHelper.psm1`.
- **Output**: JSON wrapper file with all records in a `data` array.
- **Config passed via**: ADF environment variables (`ENTITY_TYPE`, `TENANT_NAME`, `KEYVAULT_NAME`, extension attributes).
- **Auth**: Certificate from Key Vault, Graph SDK / ExchangeOnlineManagement module.
- **Storage upload**: Az.Storage module.

### Sandbox Containers

Three containers with a shared entrypoint and per-entity module pattern:

| Container | Entities (Tier 1) | Entities (Tier 2) | Entities (Tier 3) |
|-----------|-------------------|--------------------|--------------------|
| graph-ingest | entra_users, entra_groups, entra_contacts, entra_devices, entra_applications, entra_service_principals, entra_delegated_permission_grants, intune_managed_devices, mde_devices, teams_teams | entra_group_members, entra_group_owners, entra_sp_assignments, entra_sp_owners, entra_app_owners, teams_channels | entra_application_permission_grants, entra_sp_claims_mapping_policies, entra_delegated_permission_classifications, entra_sign_in_logs, entra_app_proxy_config, entra_provisioning_jobs, teams_channel_members, teams_installed_apps, teams_channel_tabs |
| exo-ingest | exo_mailboxes, exo_contacts, exo_mail_users, exo_distribution_groups, exo_unified_groups | exo_group_members | exo_mailbox_statistics, exo_mailbox_permissions |
| spo-ingest | spo_sites | -- | spo_site_permissions |

- **Shared entrypoint**: `Invoke-Ingestion.ps1` -- single script handles all 3 containers. Discovers entity modules from `entities/` directory, filters to requested entities, runs Phase 1 + optional Phase 2.
- **Entity module pattern**: Each entity is a `.psm1` file implementing a standardized interface:
  ```
  Get-EntityConfig -> { Name, Phase1, Phase2, ApiSource, OutputFile, DetailType }
  Invoke-Phase1    -> Stream enumeration -> JSONL -> upload to blob
  Invoke-Phase2    -> (Optional) Parallel enrichment via WorkerPool
  ```
- **Shared modules**: `LogHelper.psm1`, `KeyVaultHelper.psm1` (with secure zero-fill), `RetryHelper.psm1` (exponential backoff), `WorkerPool.psm1` (configurable thread pool), `StorageHelperRest.psm1` (direct REST upload to ADLS2).
- **Output**: JSONL files (flat records) + per-entity manifest JSON with `run_id`, `phase1_record_count`, `phase2_record_count`, `status`, `errors`.
- **Config passed via**: Orchestrator environment variables (`ENTITY_NAMES` comma-separated, `TENANT_KEY`, `TENANT_ID`, `ORGANIZATION`, `CLIENT_ID`, `CERT_NAME`, storage config, `MAX_PARALLELISM`, `SIGN_IN_LOOKBACK_DAYS`).
- **Auth**: Certificate from Key Vault. Per-container `Connect-ToService.ps1` (Graph SDK / EXO module / PnP.PowerShell).
- **Storage upload**: Direct ADLS2 REST API (no Az.Storage dependency).

### Entity Coverage Delta

**Entities in sandbox but not in main repo (23 net new):**

| Category | Entities |
|----------|----------|
| **Entra ID** | entra_applications, entra_service_principals, entra_delegated_permission_grants, entra_sp_assignments, entra_sp_owners, entra_app_owners, entra_application_permission_grants, entra_sp_claims_mapping_policies, entra_delegated_permission_classifications, entra_sign_in_logs, entra_app_proxy_config, entra_provisioning_jobs |
| **Intune/MDE** | intune_managed_devices, mde_devices |
| **Teams** | teams_teams, teams_channels, teams_channel_members, teams_installed_apps, teams_channel_tabs |
| **EXO** | exo_distribution_groups, exo_unified_groups, exo_group_members, exo_mailbox_statistics, exo_mailbox_permissions |
| **SPO** | spo_site_permissions |

**Entities in main repo but not yet in sandbox (need entity modules):**

| Entity | Notes |
|--------|-------|
| `ad_users` | Active Directory users (target tenant). Requires AD module or Graph equivalent. |
| `ad_contacts` | Active Directory contacts (target tenant). Same. |
| `onedrive` (via Graph) | Main repo fetches OneDrive sites via Graph API. Sandbox covers this differently via `spo_sites` but may need a dedicated equivalent. |
| `user_managers` | Main repo fetches as separate entity. Sandbox embeds manager data in `entra_users` via `$expand=manager`. **Verify equivalence.** |

---

## 4. Landing Zone Format Differences (The Critical Bridge)

This is the contract between ingestion and DLT. Both sides must agree on it.

### Path Structure

```
Main repo:     landing/{tenant_name}/{entity_type}/{date}/{entity}_{ts}.json
Sandbox:       landing/{entity_type}/{tenant_key}/{YYYY-MM-DD}/{entity}_*.jsonl
```

The hierarchy is inverted. Main repo groups by tenant first; sandbox groups by entity first. The sandbox structure is better for Auto Loader because each Bronze table reads one entity type -- entity-first means `landing/{entity}/*/` is a clean glob, while tenant-first requires `landing/*/{entity}/*/*` which is wider.

### File Format

| | Main Repo | Sandbox |
|-|----------|---------|
| **Extension** | `.json` | `.jsonl` |
| **Structure** | Single JSON object with wrapper | One JSON record per line |
| **Record access** | Records nested inside `data` array | Records are top-level |
| **Metadata** | In-file: `tenant_id`, `tenant_name`, `batch_id`, `entity_type`, `ingested_at`, `record_count` | Separate `_manifest_*.json` per entity per run |
| **Auto Loader** | Requires `multiLine: true` | Default streaming mode |

### What DLT Bronze Currently Expects (Main Repo)

From `pl_bronze.py`:

```python
# Reads wrapper + nested data array as a single row
spark.readStream.format("cloudFiles")
    .option("multiLine", "true")
    .option("cloudFiles.schemaHints", "batch_id STRING, tenant_id STRING, tenant_name STRING, ...")
    .load(f"{LANDING_BASE}/*/{entity_name}/*/*")

# Data quality expectations
@dlt.expect("has_data_array", "data IS NOT NULL")
@dlt.expect("has_batch_id", "batch_id IS NOT NULL")

# Tenant name from wrapper, fallback to path extraction
.withColumn("tenant_name", coalesce(col("tenant_name"), col("_path_tenant")))
```

Silver then **explodes** the `data` array to get individual records.

### What DLT Bronze Uses in Sandbox

From `bronze.py`:

```python
# Reads flat JSONL records directly
spark.readStream.format("cloudFiles")
    .option("cloudFiles.format", "json")
    .option("cloudFiles.inferColumnTypes", "true")
    .load(f"{BASE_PATH}/{entity_type}/*/")

# Tenant key extracted from path only (no in-file field)
.withColumn("_tenant_key", regexp_extract(
    col("_metadata.file_path"), r"/([^/]+)/\d{4}-\d{2}-\d{2}/", 1
))

# Data quality expectation
@dlt.expect("valid_tenant_key", "_tenant_key IS NOT NULL")
```

No `data` array to explode -- records arrive flat.

### Landing Format Change Impact on DLT

When the merged ingestion system writes to the sandbox format, the DLT person needs to know:

| DLT Layer | What Changes | What Stays the Same |
|-----------|-------------|-------------------|
| **Bronze** | Path glob pattern (entity-first); remove `multiLine`; remove wrapper field hints; remove `data IS NOT NULL`/`batch_id IS NOT NULL` expectations; add `_tenant_key` from path; add ~23 new entity tables | Auto Loader mechanism; `_source_file`, `_source_system`, `_dlt_ingested_at` metadata columns |
| **Silver** | Remove `data` array explosion logic; reference `_tenant_key` instead of `tenant_name` | All field names within records are identical (same API responses); all transformation logic (license detection, email normalization, dedup, environment tagging) |
| **Audit** | Batch tracking must source from manifest files instead of wrapper fields | Concept unchanged (batch = run_id from manifest) |

**The actual record data inside landing files does not change.** Both systems write the raw API response for each record. The difference is purely structural: wrapper+array vs flat JSONL. Silver's per-field transformation logic is unaffected.

---

## 5. Recommendation: Merge Sandbox Ingestion INTO Main Repo

**Direction:** Adopt the sandbox's orchestrator + containers + landing format. The main repo's DLT pipelines stay and will need targeted updates by the DLT owner to handle the new landing structure.

**Rationale:**

1. The sandbox orchestrator is programmatic, testable, and extensible. ADF pipeline JSON is hard to review, version, test, and extend.
2. The sandbox covers 2.5x more entity types, with a module pattern that makes new entities trivial to add (~50 lines of PowerShell each).
3. The tier system enables graduated scheduling (core 6h, relationships daily, enrichment weekly) without separate pipelines.
4. Two-phase enrichment is architecturally sound for parent-child relationships and has no equivalent in the main repo.
5. JSONL + entity-first paths are more efficient for Auto Loader and simpler to reason about.
6. Run/task observability blobs give operational visibility that ADF activity logs lack.

---

## 6. High-Level Merge Plan

### Phase 0: Preparation & Alignment

- [ ] **Resolve entity naming** -- Main repo ADF passes short names (`users`, `mailboxes`) as `ENTITY_TYPE` to containers, but DLT Bronze table names use `entra_users`, `exo_mailboxes`. Sandbox uses the fully-qualified names everywhere. Confirm the main repo's DLT Bronze already reads from `entra_users/` paths (it does: `create_bronze_stream("entra_users")`). This means the **current main repo containers may be writing to paths DLT is not reading** (if containers write `users/` but DLT reads `entra_users/`). Clarify how this works today before proceeding.
- [ ] **Audit entity field parity** -- For each entity that exists in both systems, compare the fields written by each container to confirm the raw API response structure is equivalent. Key concern: main repo's `user_managers` is a separate entity vs sandbox's `$expand=manager` embedded in `entra_users`. Determine if the Silver `user_managers` table needs a new source or if it can derive from the expanded user records.
- [ ] **Add missing entity modules to sandbox** -- Create `.psm1` modules for `ad_users` and `ad_contacts` (if still needed). Confirm whether `onedrive` coverage is handled by `spo_sites` or needs a separate module.
- [ ] **Define the DLT change handoff spec** -- Document exactly what changes the DLT person needs to make: new path patterns, removed wrapper fields, `_tenant_key` convention, new Bronze table definitions. This is a deliverable from this analysis.

### Phase 1: Bring Sandbox Components Into Main Repo

- [ ] Copy `ingestion-orchestrator/` (Azure Functions project + tests) into `src/analytics/ingestion-orchestrator/`
- [ ] Copy sandbox container directories (`containers/graph-ingest/`, `exo-ingest/`, `spo-ingest/`) into `src/analytics/containers/` alongside existing `analytics-ingest/` directory (keep old containers until cutover)
- [ ] Copy `shared/` modules (LogHelper, KeyVaultHelper, RetryHelper, WorkerPool, StorageHelperRest) into `src/analytics/shared/`
- [ ] Merge entity-registry.json to include any main-repo-only entities from Phase 0 gap closure
- [ ] Update `docker-compose.yml` for the new 3-container structure
- [ ] Port environment-specific config: create `tenants.json` entries from main repo's `config-source.json`/`config-target.json` + `tenant_list.json`

### Phase 2: Infrastructure & Deploy

- [ ] Add Terraform for Azure Functions app (orchestrator hosting)
- [ ] Update ACA job Terraform to provision the 3 new container jobs (graph/exo/spo) per the sandbox naming
- [ ] Configure `storage.json` pointing to the main environment's ADLS account
- [ ] Build and push new container images to ACR
- [ ] Deploy Azure Functions orchestrator to the target environment

### Phase 3: Validate & Cut Over

- [ ] Run sandbox containers against a test landing zone and verify JSONL output matches what DLT expects field-by-field
- [ ] Run both old and new ingestion systems in parallel to a separate landing path, diff the output to confirm record-level equivalence for shared entities
- [ ] Hand off the DLT change spec to the pipeline owner
- [ ] Once DLT Bronze is updated: switch the orchestrator to write to the production landing zone, disable ADF ingestion triggers
- [ ] Remove old containers (`analytics-ingest/`), old ADF ingestion pipelines (`pl_ingest_entity`, `pl_orchestrate_tenant_ingestion`, `pl_orchestrate_m365_ingestion`, `pl_orchestrate_exo_mailboxes`), and old triggers (`trig_ingest_madev1`, `trig_ingest_madev2`)
- [ ] Retain ADF pipelines for DLT triggering and Gold-to-Dataverse export (out of scope for this workstream but they stay)

---

## 7. Risk Areas

| Risk | Severity | Mitigation |
|------|----------|------------|
| **Entity field name mismatch** between old and new containers for shared entities | High | Phase 0 field parity audit. Diff actual JSONL output side-by-side for each shared entity. |
| **`user_managers` structural difference** -- separate entity vs embedded `$expand` | Medium | Decide in Phase 0: either add a `user_managers` entity module to sandbox, or confirm Silver can derive manager data from the expanded `entra_users` records. |
| **Landing path naming** confusion if main repo DLT currently reads from paths the current containers don't write to | Medium | Phase 0 investigation. May indicate the main repo has a latent bug or an undocumented rename. |
| **ACA job provisioning differences** between environments | Low | Parameterize all resource names in orchestrator config and Terraform. |
| **Azure Functions cold start** affecting timer precision | Low | Timer runs every minute and uses 59-second look-back; cold start is absorbed. |

---

## 8. DLT Change Handoff Summary

This section is a reference for the person updating DLT pipelines. It describes only what changes at the landing boundary.

**Landing path:** `{entity_type}/{tenant_key}/{YYYY-MM-DD}/` (was `{tenant_name}/{entity_type}/{date}/`)

**File format:** JSONL -- one JSON object per line (was multi-line JSON wrapper with nested `data` array)

**Metadata:** No longer in-file. `batch_id`, `tenant_id`, `tenant_name`, `entity_type`, `ingested_at`, `record_count` wrapper fields are gone. Tenant is derived from path. Batch/run info is in `_manifest_*.json` sidecar files.

**Phase 2 sub-directories:** Entity types with Phase 2 enrichment write to `{entity_type}/{tenant_key}/{date}/{detail_type}/` subdirectories (e.g., `entra_group_members/{tenant}/{date}/members/`). Bronze must account for this.

**New entities:** ~23 new entity types need Bronze table definitions. The sandbox `bronze.py` already has all 39 table definitions and can be used as a reference.

**Record contents:** Unchanged. The actual API response fields within each record are identical -- same Graph camelCase, same EXO PascalCase. All Silver transformation logic applies as-is once the `data` array explosion and `tenant_name` references are adjusted.
