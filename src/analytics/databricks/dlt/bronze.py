import dlt
from pyspark.sql.functions import col, lit, current_timestamp, regexp_extract
from pyspark.sql.types import ArrayType, BooleanType, StringType, StructField, StructType

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


def _read_landing(
    schedule_tier, entity_type, detail_type=None, file_pattern="*.jsonl", schema=None
):
    """Read JSONL files from the landing container using Auto Loader.

    Note: Do NOT set cloudFiles.schemaLocation or cloudFiles.schemaEvolutionMode
    here — DLT manages schema inference and checkpoints internally.

    Args:
        detail_type: If set, read only from the Phase 2 sub-directory (e.g. "members")
                     to avoid mixing Phase 1 group listings with Phase 2 member records.
        file_pattern: Glob pattern for file names (default "*.jsonl"). Use a specific
                      pattern like "spo_sites_*.jsonl" when Phase 2 chunks exist in
                      subdirectories and Auto Loader's recursive listing would mix schemas.
        schema: Optional StructType to provide when the landing path may be empty.
                Bypasses Auto Loader's schema inference (CF_EMPTY_DIR_FOR_SCHEMA_INFERENCE)
                so tables can be defined before data arrives. When files appear later,
                Auto Loader picks them up automatically.
    """
    if detail_type:
        landing_path = f"{BASE_PATH}/{schedule_tier}/{entity_type}/*/*/{detail_type}/"
    else:
        landing_path = f"{BASE_PATH}/{schedule_tier}/{entity_type}/*/"
    reader = (
        spark.readStream
        .format("cloudFiles")
        .option("cloudFiles.format", "json")
        .option("cloudFiles.inferColumnTypes", "true")
        .option("pathGlobFilter", file_pattern)
    )
    if schema:
        reader = reader.schema(schema)
    return (
        reader
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
# Active entities
#
# Auto Loader fails with CF_EMPTY_DIR_FOR_SCHEMA_INFERENCE when a landing
# directory has no files (e.g., Phase 2 produced no output, or the ingest
# container hasn't run yet for this tier). Entities with simple/flat schemas
# pass a fallback `schema` to _read_landing to bypass inference. Entities
# with complex nested types (structs, deeply nested arrays) omit the schema
# to preserve Auto Loader's native type inference — these always have data
# in practice since every tenant has groups, mailboxes, and sites.
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


@dlt.table(
    name="teams_teams",
    comment="Raw Teams-enabled groups (Phase 1 inventory) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def teams_teams():
    return _read_landing("core", "teams_teams", file_pattern="teams_teams_*.jsonl")


@dlt.table(
    name="teams_team_settings",
    comment="Raw Teams settings (Phase 2 per-team detail) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def teams_team_settings():
    return _read_landing("core", "teams_teams", detail_type="settings")


# --- Core enrichment tier ---

# Fallback schemas for detail_type entities whose landing subdirectories may
# not exist (Phase 2 produced no output, or the ingest container hasn't run).
# Only defined for entities with simple/flat schemas where all columns are
# strings, booleans, or simple arrays. DLT captures any extra columns from
# the JSON in _rescued_data automatically.
#
# Entities with complex nested types (exo_mailbox_statistics,
# exo_mailbox_permissions, spo_site_permissions, teams_team_settings) omit
# fallback schemas to preserve Auto Loader's native struct inference. These
# always have data in practice since every tenant has mailboxes and sites.

_ENTRA_GROUP_MEMBERS_SCHEMA = StructType(
    [
        StructField("groupId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
        StructField("userPrincipalName", StringType()),
        StructField("mail", StringType()),
        StructField("@odata.type", StringType()),
    ]
)

_EXO_GROUP_MEMBERS_SCHEMA = StructType(
    [
        StructField("groupIdentity", StringType()),
        StructField("groupObjectId", StringType()),
        StructField("groupType", StringType()),
        StructField("memberName", StringType()),
        StructField("memberObjectId", StringType()),
        StructField("memberType", StringType()),
        StructField("primarySmtp", StringType()),
    ]
)

_TEAMS_CHANNELS_SCHEMA = StructType(
    [
        StructField("teamId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
        StructField("description", StringType()),
        StructField("membershipType", StringType()),
        StructField("createdDateTime", StringType()),
        StructField("webUrl", StringType()),
        StructField("email", StringType()),
        StructField("isArchived", BooleanType()),
    ]
)

_TEAMS_CHANNEL_MEMBERS_SCHEMA = StructType(
    [
        StructField("teamId", StringType()),
        StructField("channelId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
        StructField("email", StringType()),
        StructField("roles", ArrayType(StringType())),
        StructField("@odata.type", StringType()),
    ]
)

_TEAMS_INSTALLED_APPS_SCHEMA = StructType(
    [
        StructField("teamId", StringType()),
        StructField("appId", StringType()),
        StructField("displayName", StringType()),
        StructField("teamsAppId", StringType()),
        StructField("version", StringType()),
        StructField("publishingState", StringType()),
    ]
)

_TEAMS_CHANNEL_TABS_SCHEMA = StructType(
    [
        StructField("teamId", StringType()),
        StructField("channelId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
        StructField("webUrl", StringType()),
        StructField("appDisplayName", StringType()),
        StructField("teamsAppId", StringType()),
    ]
)


@dlt.table(
    name="entra_group_members",
    comment="Raw entra_group_members from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "groupId IS NOT NULL")
def entra_group_members():
    return _read_landing(
        "core_enrichment",
        "entra_group_members",
        detail_type="members",
        schema=_ENTRA_GROUP_MEMBERS_SCHEMA,
    )


@dlt.table(
    name="exo_group_members",
    comment="Raw Exchange Online group memberships from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "groupIdentity IS NOT NULL")
def exo_group_members():
    return _read_landing(
        "core_enrichment",
        "exo_group_members",
        detail_type="members",
        schema=_EXO_GROUP_MEMBERS_SCHEMA,
    )


@dlt.table(
    name="teams_channels",
    comment="Raw Teams channels (all types) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "teamId IS NOT NULL")
def teams_channels():
    return _read_landing(
        "core_enrichment",
        "teams_channels",
        detail_type="channels",
        schema=_TEAMS_CHANNELS_SCHEMA,
    )


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


@dlt.table(
    name="teams_channel_members",
    comment="Raw Teams private/shared channel members from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "teamId IS NOT NULL")
def teams_channel_members():
    return _read_landing(
        "enrichment",
        "teams_channel_members",
        detail_type="members",
        schema=_TEAMS_CHANNEL_MEMBERS_SCHEMA,
    )


@dlt.table(
    name="teams_installed_apps",
    comment="Raw Teams installed apps from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "teamId IS NOT NULL")
def teams_installed_apps():
    return _read_landing(
        "enrichment",
        "teams_installed_apps",
        detail_type="apps",
        schema=_TEAMS_INSTALLED_APPS_SCHEMA,
    )


@dlt.table(
    name="teams_channel_tabs",
    comment="Raw Teams channel tabs from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "teamId IS NOT NULL")
def teams_channel_tabs():
    return _read_landing(
        "enrichment",
        "teams_channel_tabs",
        detail_type="tabs",
        schema=_TEAMS_CHANNEL_TABS_SCHEMA,
    )
