import dlt
from pyspark.sql.functions import col, lit, current_timestamp, regexp_extract

STORAGE_ACCOUNT = spark.conf.get("analytics.storage_account_name")
LANDING_CONTAINER = "landing"
BASE_PATH = f"abfss://{LANDING_CONTAINER}@{STORAGE_ACCOUNT}.dfs.core.windows.net"

# Delta retention — controls how far back time travel queries work.
# Both settings must cover the same window: the log tracks which files
# belong to each version, and the files themselves must still exist.
DELTA_LOG_RETENTION = "interval 90 days"
DELTA_DELETED_FILE_RETENTION = "interval 90 days"

BRONZE_TABLE_PROPERTIES = {
    "quality": "bronze",
    "pipelines.autoOptimize.managed": "true",
    "delta.logRetentionDuration": DELTA_LOG_RETENTION,
    "delta.deletedFileRetentionDuration": DELTA_DELETED_FILE_RETENTION,
}


def _read_landing(schedule_tier, entity_type, detail_type=None, file_pattern="*.jsonl"):
    """Read JSONL files from the landing container using Auto Loader.

    Note: Do NOT set cloudFiles.schemaLocation or cloudFiles.schemaEvolutionMode
    here — DLT manages schema inference and checkpoints internally.

    Args:
        detail_type: If set, read only from the Phase 2 sub-directory (e.g. "members")
                     to avoid mixing Phase 1 group listings with Phase 2 member records.
        file_pattern: Glob pattern for file names (default "*.jsonl"). Use a specific
                      pattern like "spo_sites_*.jsonl" when Phase 2 chunks exist in
                      subdirectories and Auto Loader's recursive listing would mix schemas.
    """
    if detail_type:
        landing_path = f"{BASE_PATH}/{schedule_tier}/{entity_type}/*/*/{detail_type}/"
    else:
        landing_path = f"{BASE_PATH}/{schedule_tier}/{entity_type}/*/"
    return (
        spark.readStream
        .format("cloudFiles")
        .option("cloudFiles.format", "json")
        .option("cloudFiles.inferColumnTypes", "true")
        .option("pathGlobFilter", file_pattern)
        .load(landing_path)
        .withColumn(
            "_tenant_key",
            regexp_extract(col("_metadata.file_path"), r"/([^/]+)/\d{4}-\d{2}-\d{2}/", 1),
        )
        .withColumn("_source_file", col("_metadata.file_path"))
        .withColumn("_source_system", lit("graph_api"))
        .withColumn("_schedule_tier", lit(schedule_tier))
        .withColumn("_dlt_ingested_at", current_timestamp())
    )


# ============================================================================
# Active entities — these have landing data and are processed by DLT.
# Uncomment additional entities below as their ingestion is configured.
#
# DLT fails the entire pipeline if Auto Loader can't find the landing
# directory for any defined table, so only define entities with data.
# ============================================================================


# --- Core tier ---

@dlt.table(
    name="entra_users",
    comment="Raw entra_users data from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_users():
    return _read_landing("core", "entra_users")


@dlt.table(
    name="entra_groups",
    comment="Raw entra_groups data from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_groups():
    return _read_landing("core", "entra_groups")


@dlt.table(
    name="entra_contacts",
    comment="Raw entra_contacts (organizational contacts) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_contacts():
    return _read_landing("core", "entra_contacts")


@dlt.table(
    name="exo_mailboxes",
    comment="Raw Exchange Online mailboxes from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExchangeGuid IS NOT NULL")
def exo_mailboxes():
    return _read_landing("core", "exo_mailboxes")


@dlt.table(
    name="exo_contacts",
    comment="Raw Exchange Online mail contacts from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_contacts():
    return _read_landing("core", "exo_contacts")


@dlt.table(
    name="exo_mail_users",
    comment="Raw Exchange Online mail users from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_mail_users():
    return _read_landing("core", "exo_mail_users")


@dlt.table(
    name="exo_distribution_groups",
    comment="Raw Exchange Online distribution groups from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_distribution_groups():
    return _read_landing("core", "exo_distribution_groups")


@dlt.table(
    name="exo_unified_groups",
    comment="Raw Exchange Online unified (M365) groups from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_unified_groups():
    return _read_landing("core", "exo_unified_groups")


@dlt.table(
    name="spo_sites",
    comment="Raw SharePoint Online sites from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def spo_sites():
    return _read_landing("core", "spo_sites", file_pattern="spo_sites_*.jsonl")


@dlt.table(
    name="spo_site_usage",
    comment="Raw SharePoint site usage (storage, item counts) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "siteUrl IS NOT NULL")
def spo_site_usage():
    return _read_landing("core", "spo_sites", detail_type="usage")


# --- Core enrichment tier ---


@dlt.table(
    name="entra_group_members",
    comment="Raw entra_group_members from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "groupId IS NOT NULL")
def entra_group_members():
    return _read_landing("core_enrichment", "entra_group_members", detail_type="members")


@dlt.table(
    name="exo_group_members",
    comment="Raw Exchange Online group memberships from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "groupIdentity IS NOT NULL")
def exo_group_members():
    return _read_landing("core_enrichment", "exo_group_members", detail_type="members")


# --- Enrichment tier ---


@dlt.table(
    name="exo_mailbox_statistics",
    comment="Raw Exchange Online mailbox statistics from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "MailboxGuid IS NOT NULL")
def exo_mailbox_statistics():
    return _read_landing("enrichment", "exo_mailbox_statistics", detail_type="statistics")


@dlt.table(
    name="spo_site_permissions",
    comment="Raw SPO site permissions, groups, sharing links from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "siteUrl IS NOT NULL")
def spo_site_permissions():
    return _read_landing("enrichment", "spo_site_permissions", detail_type="permissions")


@dlt.table(
    name="exo_mailbox_permissions",
    comment="Raw Exchange Online mailbox permissions from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "exchangeGuid IS NOT NULL")
def exo_mailbox_permissions():
    return _read_landing("enrichment", "exo_mailbox_permissions", detail_type="permissions")
