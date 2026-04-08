import dlt
from pyspark.sql.functions import col, current_timestamp, lit, regexp_extract
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


# Minimal fallback schema for entities without explicit schemas. Prevents
# CF_EMPTY_DIR_FOR_SCHEMA_INFERENCE when landing directories are empty.
# Auto Loader infers the real schema once files arrive; extra columns
# beyond this stub go to _rescued_data and are picked up on next refresh.
_FALLBACK_SCHEMA = StructType([StructField("id", StringType())])


def _read_landing(entity_type, detail_type=None, file_pattern="*.jsonl", schema=None):
    """Read JSONL files from the landing container using Auto Loader.

    Note: Do NOT set cloudFiles.schemaLocation or cloudFiles.schemaEvolutionMode
    here — DLT manages schema inference and checkpoints internally.

    Args:
        entity_type: Entity name used as the top-level directory in the landing zone.
        detail_type: If set, read only from the Phase 2 sub-directory (e.g. "members")
                     to avoid mixing Phase 1 group listings with Phase 2 member records.
        file_pattern: Glob pattern for file names (default "*.jsonl"). Use a specific
                      pattern like "spo_sites_*.jsonl" when Phase 2 chunks exist in
                      subdirectories and Auto Loader's recursive listing would mix schemas.
        schema: Optional StructType to provide when the landing path may be empty.
                Bypasses Auto Loader's schema inference so tables can be defined
                before data arrives. Falls back to _FALLBACK_SCHEMA if not specified.
    """
    if detail_type:
        landing_path = f"{BASE_PATH}/{entity_type}/*/*/{detail_type}/"
    else:
        landing_path = f"{BASE_PATH}/{entity_type}/*/"
    reader = (
        spark.readStream
        .format("cloudFiles")
        .option("cloudFiles.format", "json")
        .option("cloudFiles.inferColumnTypes", "true")
        .option("pathGlobFilter", file_pattern)
        .schema(schema or _FALLBACK_SCHEMA)
    )
    return (
        reader
        .load(landing_path)
        .withColumn(
            "_tenant_key",
            regexp_extract(col("_metadata.file_path"), r"/([^/]+)/\d{4}-\d{2}-\d{2}/", 1),
        )
        .withColumn("_source_file", col("_metadata.file_path"))
        .withColumn("_source_system", lit("graph_api"))
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


# --- Tier 1: Core entities ---

@dlt.table(
    name="entra_users",
    comment="Raw entra_users data from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_users():
    return _read_landing("entra_users")


@dlt.table(
    name="entra_groups",
    comment="Raw entra_groups data from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_groups():
    return _read_landing("entra_groups")


@dlt.table(
    name="entra_contacts",
    comment="Raw entra_contacts (organizational contacts) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_contacts():
    return _read_landing("entra_contacts")


@dlt.table(
    name="exo_mailboxes",
    comment="Raw Exchange Online mailboxes from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExchangeGuid IS NOT NULL")
def exo_mailboxes():
    return _read_landing("exo_mailboxes")


@dlt.table(
    name="exo_contacts",
    comment="Raw Exchange Online mail contacts from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_contacts():
    return _read_landing("exo_contacts")


@dlt.table(
    name="exo_mail_users",
    comment="Raw Exchange Online mail users from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_mail_users():
    return _read_landing("exo_mail_users")


@dlt.table(
    name="exo_distribution_groups",
    comment="Raw Exchange Online distribution groups from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_distribution_groups():
    return _read_landing("exo_distribution_groups")


@dlt.table(
    name="exo_unified_groups",
    comment="Raw Exchange Online unified (M365) groups from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "ExternalDirectoryObjectId IS NOT NULL")
def exo_unified_groups():
    return _read_landing("exo_unified_groups")


@dlt.table(
    name="spo_sites",
    comment="Raw SharePoint Online sites from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def spo_sites():
    return _read_landing("spo_sites", file_pattern="spo_sites_*.jsonl")


# spo_site_usage commented out — requires spo_sites Phase 2 "usage" subdirectory
# which only exists after SPO enrichment runs. Uncomment once enrichment data lands.
# @dlt.table(
#     name="spo_site_usage",
#     comment="Raw SharePoint site usage (storage, item counts) from all tenants",
#     table_properties=BRONZE_TABLE_PROPERTIES,
# )
# @dlt.expect("valid_record", "siteUrl IS NOT NULL")
# def spo_site_usage():
#     return _read_landing("spo_sites", detail_type="usage")


@dlt.table(
    name="teams_teams",
    comment="Raw Teams-enabled groups (Phase 1 inventory) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def teams_teams():
    return _read_landing("teams_teams", file_pattern="teams_teams_*.jsonl")


# teams_team_settings commented out — requires teams_teams Phase 2 "settings" subdirectory
# which only exists after Teams enrichment runs. Uncomment once enrichment data lands.
# @dlt.table(
#     name="teams_team_settings",
#     comment="Raw Teams settings (Phase 2 per-team detail) from all tenants",
#     table_properties=BRONZE_TABLE_PROPERTIES,
# )
# @dlt.expect("valid_record", "id IS NOT NULL")
# def teams_team_settings():
#     return _read_landing("teams_teams", detail_type="settings")


_ENTRA_DEVICES_SCHEMA = StructType(
    [
        StructField("id", StringType()),
        StructField("deviceId", StringType()),
        StructField("displayName", StringType()),
        StructField("operatingSystem", StringType()),
        StructField("operatingSystemVersion", StringType()),
        StructField("trustType", StringType()),
        StructField("isManaged", BooleanType()),
        StructField("isCompliant", BooleanType()),
        StructField("accountEnabled", BooleanType()),
        StructField("approximateLastSignInDateTime", StringType()),
        StructField("createdDateTime", StringType()),
        StructField("model", StringType()),
        StructField("manufacturer", StringType()),
        StructField("profileType", StringType()),
        StructField("deviceCategory", StringType()),
        StructField("enrollmentProfileName", StringType()),
        StructField("onPremisesSyncEnabled", BooleanType()),
        StructField("onPremisesLastSyncDateTime", StringType()),
        StructField("onPremisesSecurityIdentifier", StringType()),
        StructField("mdmAppId", StringType()),
        StructField("registrationDateTime", StringType()),
    ]
)

_INTUNE_MANAGED_DEVICES_SCHEMA = StructType(
    [
        StructField("id", StringType()),
        StructField("deviceName", StringType()),
        StructField("managedDeviceOwnerType", StringType()),
        StructField("enrolledDateTime", StringType()),
        StructField("lastSyncDateTime", StringType()),
        StructField("operatingSystem", StringType()),
        StructField("complianceState", StringType()),
        StructField("jailBroken", StringType()),
        StructField("managementAgent", StringType()),
        StructField("osVersion", StringType()),
        StructField("azureADRegistered", BooleanType()),
        StructField("deviceEnrollmentType", StringType()),
        StructField("emailAddress", StringType()),
        StructField("azureADDeviceId", StringType()),
        StructField("deviceRegistrationState", StringType()),
        StructField("isEncrypted", BooleanType()),
        StructField("userPrincipalName", StringType()),
        StructField("model", StringType()),
        StructField("manufacturer", StringType()),
        StructField("serialNumber", StringType()),
        StructField("userId", StringType()),
        StructField("userDisplayName", StringType()),
        StructField("totalStorageSpaceInBytes", StringType()),
        StructField("freeStorageSpaceInBytes", StringType()),
        StructField("managedDeviceName", StringType()),
        StructField("partnerReportedThreatState", StringType()),
        StructField("autopilotEnrolled", BooleanType()),
        StructField("isSupervised", BooleanType()),
    ]
)


@dlt.table(
    name="entra_devices",
    comment="Raw Entra ID device objects from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_devices():
    return _read_landing("entra_devices", schema=_ENTRA_DEVICES_SCHEMA)


@dlt.table(
    name="intune_managed_devices",
    comment="Raw Intune managed devices from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def intune_managed_devices():
    return _read_landing(
        "intune_managed_devices", schema=_INTUNE_MANAGED_DEVICES_SCHEMA
    )


_MDE_DEVICES_SCHEMA = StructType(
    [
        StructField("id", StringType()),
        StructField("computerDnsName", StringType()),
        StructField("firstSeen", StringType()),
        StructField("lastSeen", StringType()),
        StructField("osPlatform", StringType()),
        StructField("osArchitecture", StringType()),
        StructField("version", StringType()),
        StructField("osBuild", StringType()),
        StructField("lastIpAddress", StringType()),
        StructField("lastExternalIpAddress", StringType()),
        StructField("healthStatus", StringType()),
        StructField("onboardingStatus", StringType()),
        StructField("riskScore", StringType()),
        StructField("exposureLevel", StringType()),
        StructField("aadDeviceId", StringType()),
        StructField("machineTags", ArrayType(StringType())),
        StructField("rbacGroupName", StringType()),
        StructField("rbacGroupId", StringType()),
        StructField("deviceValue", StringType()),
    ]
)


@dlt.table(
    name="mde_devices",
    comment="Raw Microsoft Defender for Endpoint device inventory from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def mde_devices():
    return _read_landing("mde_devices", schema=_MDE_DEVICES_SCHEMA)


@dlt.table(
    name="entra_applications",
    comment="Raw Entra app registrations from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_applications():
    return _read_landing("entra_applications")


@dlt.table(
    name="entra_service_principals",
    comment="Raw Entra service principals (enterprise apps) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_service_principals():
    return _read_landing("entra_service_principals")


_ENTRA_DELEGATED_PERMISSION_GRANTS_SCHEMA = StructType(
    [
        StructField("id", StringType()),
        StructField("clientId", StringType()),
        StructField("consentType", StringType()),
        StructField("principalId", StringType()),
        StructField("resourceId", StringType()),
        StructField("scope", StringType()),
    ]
)


@dlt.table(
    name="entra_delegated_permission_grants",
    comment="Raw OAuth2 delegated permission grants from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_delegated_permission_grants():
    return _read_landing(
        "entra_delegated_permission_grants", schema=_ENTRA_DELEGATED_PERMISSION_GRANTS_SCHEMA
    )


# --- Tier 2: Core enrichment entities ---

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

_ENTRA_GROUP_OWNERS_SCHEMA = StructType(
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
        "entra_group_members",
        detail_type="members",
        schema=_ENTRA_GROUP_MEMBERS_SCHEMA,
    )


@dlt.table(
    name="entra_group_owners",
    comment="Raw entra_group_owners from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "groupId IS NOT NULL")
def entra_group_owners():
    return _read_landing(
        "entra_group_owners",
        detail_type="owners",
        schema=_ENTRA_GROUP_OWNERS_SCHEMA,
    )


@dlt.table(
    name="exo_group_members",
    comment="Raw Exchange Online group memberships from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "groupIdentity IS NOT NULL")
def exo_group_members():
    return _read_landing(
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
        "teams_channels",
        detail_type="channels",
        schema=_TEAMS_CHANNELS_SCHEMA,
    )


_ENTRA_SP_ASSIGNMENTS_SCHEMA = StructType(
    [
        StructField("servicePrincipalId", StringType()),
        StructField("id", StringType()),
        StructField("appRoleId", StringType()),
        StructField("principalDisplayName", StringType()),
        StructField("principalId", StringType()),
        StructField("principalType", StringType()),
        StructField("resourceDisplayName", StringType()),
        StructField("resourceId", StringType()),
        StructField("createdDateTime", StringType()),
    ]
)

_ENTRA_SP_OWNERS_SCHEMA = StructType(
    [
        StructField("servicePrincipalId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
        StructField("userPrincipalName", StringType()),
        StructField("mail", StringType()),
        StructField("@odata.type", StringType()),
    ]
)

_ENTRA_APP_OWNERS_SCHEMA = StructType(
    [
        StructField("applicationId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
        StructField("userPrincipalName", StringType()),
        StructField("mail", StringType()),
        StructField("@odata.type", StringType()),
    ]
)


@dlt.table(
    name="entra_sp_assignments",
    comment="Raw user/group/SP assignments to enterprise apps from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "servicePrincipalId IS NOT NULL")
def entra_sp_assignments():
    return _read_landing(
        "entra_sp_assignments",
        detail_type="assignments",
        schema=_ENTRA_SP_ASSIGNMENTS_SCHEMA,
    )


@dlt.table(
    name="entra_sp_owners",
    comment="Raw service principal owners from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "servicePrincipalId IS NOT NULL")
def entra_sp_owners():
    return _read_landing(
        "entra_sp_owners",
        detail_type="owners",
        schema=_ENTRA_SP_OWNERS_SCHEMA,
    )


@dlt.table(
    name="entra_app_owners",
    comment="Raw application registration owners from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "applicationId IS NOT NULL")
def entra_app_owners():
    return _read_landing(
        "entra_app_owners",
        detail_type="owners",
        schema=_ENTRA_APP_OWNERS_SCHEMA,
    )


# --- Tier 3: Enrichment entities ---


@dlt.table(
    name="exo_mailbox_statistics",
    comment="Raw Exchange Online mailbox statistics from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "MailboxGuid IS NOT NULL")
def exo_mailbox_statistics():
    return _read_landing("exo_mailbox_statistics", detail_type="statistics")


@dlt.table(
    name="spo_site_permissions",
    comment="Raw SPO site permissions, groups, sharing links from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "siteUrl IS NOT NULL")
def spo_site_permissions():
    return _read_landing("spo_site_permissions", detail_type="permissions")


@dlt.table(
    name="exo_mailbox_permissions",
    comment="Raw Exchange Online mailbox permissions from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "exchangeGuid IS NOT NULL")
def exo_mailbox_permissions():
    return _read_landing("exo_mailbox_permissions", detail_type="permissions")


@dlt.table(
    name="teams_channel_members",
    comment="Raw Teams private/shared channel members from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "teamId IS NOT NULL")
def teams_channel_members():
    return _read_landing(
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
        "teams_channel_tabs",
        detail_type="tabs",
        schema=_TEAMS_CHANNEL_TABS_SCHEMA,
    )


# --- Tier 3: Entra Applications ---

_ENTRA_APPLICATION_PERMISSION_GRANTS_SCHEMA = StructType(
    [
        StructField("servicePrincipalId", StringType()),
        StructField("id", StringType()),
        StructField("appRoleId", StringType()),
        StructField("principalDisplayName", StringType()),
        StructField("principalId", StringType()),
        StructField("principalType", StringType()),
        StructField("resourceDisplayName", StringType()),
        StructField("resourceId", StringType()),
        StructField("createdDateTime", StringType()),
    ]
)

_ENTRA_SP_CLAIMS_MAPPING_POLICIES_SCHEMA = StructType(
    [
        StructField("servicePrincipalId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
        StructField("definition", ArrayType(StringType())),
        StructField("isOrganizationDefault", BooleanType()),
    ]
)

_ENTRA_APP_PROXY_CONFIG_SCHEMA = StructType(
    [
        StructField("applicationId", StringType()),
        StructField("id", StringType()),
        StructField("displayName", StringType()),
    ]
)

_ENTRA_DELEGATED_PERMISSION_CLASSIFICATIONS_SCHEMA = StructType(
    [
        StructField("servicePrincipalId", StringType()),
        StructField("id", StringType()),
        StructField("permissionId", StringType()),
        StructField("permissionName", StringType()),
        StructField("classification", StringType()),
    ]
)


@dlt.table(
    name="entra_application_permission_grants",
    comment="Raw application permissions granted to service principals from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "servicePrincipalId IS NOT NULL")
def entra_application_permission_grants():
    return _read_landing(
        "entra_application_permission_grants",
        detail_type="app_role_assignments",
        schema=_ENTRA_APPLICATION_PERMISSION_GRANTS_SCHEMA,
    )


@dlt.table(
    name="entra_sp_claims_mapping_policies",
    comment="Raw claims mapping policies per service principal from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "servicePrincipalId IS NOT NULL")
def entra_sp_claims_mapping_policies():
    return _read_landing(
        "entra_sp_claims_mapping_policies",
        detail_type="claims_policies",
        schema=_ENTRA_SP_CLAIMS_MAPPING_POLICIES_SCHEMA,
    )


@dlt.table(
    name="entra_delegated_permission_classifications",
    comment="Raw delegated permission classifications per service principal from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "servicePrincipalId IS NOT NULL")
def entra_delegated_permission_classifications():
    return _read_landing(
        "entra_delegated_permission_classifications",
        detail_type="perm_classifications",
        schema=_ENTRA_DELEGATED_PERMISSION_CLASSIFICATIONS_SCHEMA,
    )


@dlt.table(
    name="entra_sign_in_logs",
    comment="Raw Entra sign-in logs from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "id IS NOT NULL")
def entra_sign_in_logs():
    return _read_landing("entra_sign_in_logs")


@dlt.table(
    name="entra_app_proxy_config",
    comment="Raw Application Proxy configuration (beta) from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "applicationId IS NOT NULL")
def entra_app_proxy_config():
    return _read_landing(
        "entra_app_proxy_config",
        detail_type="proxy_config",
        schema=_ENTRA_APP_PROXY_CONFIG_SCHEMA,
    )


_ENTRA_PROVISIONING_JOBS_SCHEMA = StructType(
    [
        StructField("servicePrincipalId", StringType()),
        StructField("id", StringType()),
        StructField("templateId", StringType()),
    ]
)


@dlt.table(
    name="entra_provisioning_jobs",
    comment="Raw provisioning/synchronization jobs per service principal from all tenants",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
@dlt.expect("valid_record", "servicePrincipalId IS NOT NULL")
def entra_provisioning_jobs():
    return _read_landing(
        "entra_provisioning_jobs",
        detail_type="sync_jobs",
        schema=_ENTRA_PROVISIONING_JOBS_SCHEMA,
    )


# --- Orchestrator run history ---


@dlt.table(
    name="orchestrator_runs",
    comment="Ingestion orchestrator run history",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
def orchestrator_runs():
    return _read_landing("_orchestrator/runs")


@dlt.table(
    name="orchestrator_tasks",
    comment="Ingestion orchestrator task history (per tenant x container dispatch)",
    table_properties=BRONZE_TABLE_PROPERTIES,
)
def orchestrator_tasks():
    return _read_landing("_orchestrator/tasks")
