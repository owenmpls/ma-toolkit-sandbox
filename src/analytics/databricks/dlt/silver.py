import dlt
from pyspark.sql.functions import (
    col,
    concat_ws,
    expr,
    lit,
    lower,
    trim,
    when,
)

# Cross-pipeline reads use spark.readStream.table() (not dlt.read_stream()),
# since bronze and silver are separate DLT pipelines.
#
# Do NOT use conditional blocks (if spark.catalog.tableExists(...)) at module
# level — DLT's static analysis conflicts with runtime catalog checks.
#
# Only define silver tables whose bronze sources exist. Uncomment entities
# below as their corresponding bronze tables are activated.


# ============================================================================
# Active entities
# ============================================================================


# --- Users ---

@dlt.view(name="v_users")
def v_users():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_users")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("userPrincipalName").alias("user_principal_name"),
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
            col("userType").alias("user_type"),
            col("onPremisesSyncEnabled").alias("on_premises_sync_enabled"),
            col("proxyAddresses").alias("proxy_addresses"),
            col("onPremisesLastSyncDateTime").alias("on_premises_last_sync"),
            col("createdDateTime").alias("created_at"),
            when(
                expr(
                    "exists(assignedLicenses, x -> x.skuId = '06ebc4ee-1bb5-47dd-8120-11324bc54e06')"
                ),
                lit("E5"),
            )
            .when(
                expr(
                    "exists(assignedLicenses, x -> x.skuId = '05e9a617-0261-4cee-bb44-138d3ef5d965')"
                ),
                lit("E3"),
            )
            .when(
                expr(
                    "exists(assignedLicenses, x -> x.skuId = '66b55226-6b4f-492c-910c-a3b7a3c9d993')"
                ),
                lit("F3"),
            )
            .when(
                expr(
                    "exists(assignedLicenses, x -> x.skuId = '18181a46-0d4e-45cd-891e-60aabd171b4e')"
                ),
                lit("E1"),
            )
            .otherwise(lit(None))
            .alias("license_type"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="users",
    comment="Cleaned and deduplicated Entra users across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="users",
    source="v_users",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Groups ---

@dlt.view(name="v_groups")
def v_groups():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_groups")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("displayName").alias("display_name"),
            col("description"),
            lower(trim(col("mail"))).alias("mail"),
            col("mailEnabled").alias("mail_enabled"),
            col("mailNickname").alias("mail_nickname"),
            col("securityEnabled").alias("security_enabled"),
            col("visibility"),
            when(
                expr("array_contains(groupTypes, 'Unified')"), lit("Microsoft 365")
            )
            .when(
                col("mailEnabled") & col("securityEnabled"),
                lit("Mail-enabled Security"),
            )
            .when(col("mailEnabled"), lit("Distribution"))
            .when(col("securityEnabled"), lit("Security"))
            .otherwise(lit("Other"))
            .alias("group_type"),
            col("membershipRule").alias("membership_rule"),
            col("membershipRuleProcessingState").alias(
                "membership_rule_processing_state"
            ),
            col("proxyAddresses").alias("proxy_addresses"),
            col("onPremisesSyncEnabled").alias("on_premises_sync_enabled"),
            col("onPremisesLastSyncDateTime").alias("on_premises_last_sync"),
            col("createdDateTime").alias("created_at"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="groups",
    comment="Cleaned and deduplicated Entra groups across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="groups",
    source="v_groups",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Mailboxes ---


@dlt.view(name="v_mailboxes")
def v_mailboxes():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.exo_mailboxes")
        .select(
            concat_ws("_", col("_tenant_key"), col("ExchangeGuid")).alias(
                "_scd_key"
            ),
            col("_tenant_key").alias("tenant_key"),
            # --- Identity ---
            col("ExchangeGuid").alias("exchange_guid"),
            col("ExternalDirectoryObjectId").alias(
                "external_directory_object_id"
            ),
            col("UserPrincipalName").alias("user_principal_name"),
            lower(trim(col("PrimarySmtpAddress"))).alias(
                "primary_smtp_address"
            ),
            col("DisplayName").alias("display_name"),
            col("Alias").alias("alias"),
            col("EmailAddresses").alias("email_addresses"),
            # --- Type & status ---
            col("RecipientType").alias("recipient_type"),
            col("RecipientTypeDetails").alias("recipient_type_details"),
            col("Database").alias("database"),
            col("WhenCreated").alias("when_created"),
            col("WhenMailboxCreated").alias("when_mailbox_created"),
            col("IsMailboxEnabled").alias("is_mailbox_enabled"),
            col("HiddenFromAddressListsEnabled").alias(
                "hidden_from_address_lists"
            ),
            # --- Resource ---
            col("IsResource").alias("is_resource"),
            col("ResourceType").alias("resource_type"),
            col("ResourceCapacity").alias("resource_capacity"),
            col("RoomMailboxAccountEnabled").alias(
                "room_mailbox_account_enabled"
            ),
            # --- Forwarding ---
            col("ForwardingAddress").alias("forwarding_address"),
            col("ForwardingSmtpAddress").alias("forwarding_smtp_address"),
            col("DeliverToMailboxAndForward").alias(
                "deliver_to_mailbox_and_forward"
            ),
            # --- Delegation ---
            col("GrantSendOnBehalfTo").alias("grant_send_on_behalf_to"),
            col("MessageCopyForSendOnBehalfEnabled").alias(
                "message_copy_for_send_on_behalf"
            ),
            col("MessageCopyForSentAsEnabled").alias(
                "message_copy_for_sent_as"
            ),
            # --- Archive ---
            col("ArchiveStatus").alias("archive_status"),
            col("ArchiveState").alias("archive_state"),
            col("ArchiveGuid").alias("archive_guid"),
            col("ArchiveName").alias("archive_name"),
            col("AutoExpandingArchiveEnabled").alias(
                "auto_expanding_archive"
            ),
            # --- Compliance holds ---
            col("LitigationHoldEnabled").alias("litigation_hold_enabled"),
            col("LitigationHoldDate").alias("litigation_hold_date"),
            col("LitigationHoldOwner").alias("litigation_hold_owner"),
            col("LitigationHoldDuration").alias("litigation_hold_duration"),
            col("InPlaceHolds").alias("in_place_holds"),
            col("ComplianceTagHoldApplied").alias(
                "compliance_tag_hold_applied"
            ),
            col("DelayHoldApplied").alias("delay_hold_applied"),
            # --- Retention ---
            col("RetentionPolicy").alias("retention_policy"),
            col("RetentionHoldEnabled").alias("retention_hold_enabled"),
            col("RetainDeletedItemsFor").alias("retain_deleted_items_for"),
            col("SingleItemRecoveryEnabled").alias(
                "single_item_recovery_enabled"
            ),
            # --- Quotas ---
            col("IssueWarningQuota").alias("issue_warning_quota"),
            col("ProhibitSendQuota").alias("prohibit_send_quota"),
            col("ProhibitSendReceiveQuota").alias(
                "prohibit_send_receive_quota"
            ),
            col("UseDatabaseQuotaDefaults").alias(
                "use_database_quota_defaults"
            ),
            # --- Transport limits ---
            col("MaxReceiveSize").alias("max_receive_size"),
            col("MaxSendSize").alias("max_send_size"),
            col("RecipientLimits").alias("recipient_limits"),
            # --- Migration state ---
            col("MailboxMoveStatus").alias("mailbox_move_status"),
            col("MailboxMoveBatchName").alias("mailbox_move_batch_name"),
            col("MailboxMoveRemoteHostName").alias(
                "mailbox_move_remote_host_name"
            ),
            col("MailboxMoveFlags").alias("mailbox_move_flags"),
            # --- Soft-delete / inactive ---
            col("IsInactiveMailbox").alias("is_inactive_mailbox"),
            col("IsSoftDeletedByDisable").alias(
                "is_soft_deleted_by_disable"
            ),
            col("IsSoftDeletedByRemove").alias(
                "is_soft_deleted_by_remove"
            ),
            col("WhenSoftDeleted").alias("when_soft_deleted"),
            # --- Custom attributes ---
            col("CustomAttribute1").alias("custom_attribute_1"),
            col("CustomAttribute2").alias("custom_attribute_2"),
            col("CustomAttribute3").alias("custom_attribute_3"),
            col("CustomAttribute4").alias("custom_attribute_4"),
            col("CustomAttribute5").alias("custom_attribute_5"),
            col("CustomAttribute6").alias("custom_attribute_6"),
            col("CustomAttribute7").alias("custom_attribute_7"),
            col("CustomAttribute8").alias("custom_attribute_8"),
            col("CustomAttribute9").alias("custom_attribute_9"),
            col("CustomAttribute10").alias("custom_attribute_10"),
            col("CustomAttribute11").alias("custom_attribute_11"),
            col("CustomAttribute12").alias("custom_attribute_12"),
            col("CustomAttribute13").alias("custom_attribute_13"),
            col("CustomAttribute14").alias("custom_attribute_14"),
            col("CustomAttribute15").alias("custom_attribute_15"),
            col("ExtensionCustomAttribute1").alias(
                "extension_custom_attribute_1"
            ),
            col("ExtensionCustomAttribute2").alias(
                "extension_custom_attribute_2"
            ),
            col("ExtensionCustomAttribute3").alias(
                "extension_custom_attribute_3"
            ),
            col("ExtensionCustomAttribute4").alias(
                "extension_custom_attribute_4"
            ),
            col("ExtensionCustomAttribute5").alias(
                "extension_custom_attribute_5"
            ),
            # --- Metadata ---
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="mailboxes",
    comment="Cleaned Exchange Online mailboxes across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="mailboxes",
    source="v_mailboxes",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- EXO Contacts ---


@dlt.view(name="v_exo_contacts")
def v_exo_contacts():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.exo_contacts")
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("ExternalDirectoryObjectId")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("ExternalDirectoryObjectId").alias(
                "external_directory_object_id"
            ),
            col("DisplayName").alias("display_name"),
            lower(trim(col("PrimarySmtpAddress"))).alias(
                "primary_smtp_address"
            ),
            col("RecipientType").alias("recipient_type"),
            col("Alias").alias("alias"),
            col("ExternalEmailAddress").alias("external_email_address"),
            col("EmailAddresses").alias("email_addresses"),
            col("HiddenFromAddressListsEnabled").alias(
                "hidden_from_address_lists"
            ),
            col("WhenCreated").alias("when_created"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="exo_contacts",
    comment="Cleaned Exchange Online mail contacts across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="exo_contacts",
    source="v_exo_contacts",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Distribution Groups ---


@dlt.view(name="v_distribution_groups")
def v_distribution_groups():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.exo_distribution_groups"
        )
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("ExternalDirectoryObjectId")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("ExternalDirectoryObjectId").alias(
                "external_directory_object_id"
            ),
            col("DisplayName").alias("display_name"),
            lower(trim(col("PrimarySmtpAddress"))).alias(
                "primary_smtp_address"
            ),
            col("Alias").alias("alias"),
            col("GroupType").alias("group_type"),
            col("ManagedBy").alias("managed_by"),
            col("EmailAddresses").alias("email_addresses"),
            col("HiddenFromAddressListsEnabled").alias(
                "hidden_from_address_lists"
            ),
            col("RequireSenderAuthenticationEnabled").alias(
                "require_sender_auth"
            ),
            col("WhenCreated").alias("when_created"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="distribution_groups",
    comment="Cleaned Exchange Online distribution groups across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="distribution_groups",
    source="v_distribution_groups",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Unified Groups (Microsoft 365 Groups) ---


@dlt.view(name="v_unified_groups")
def v_unified_groups():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.exo_unified_groups"
        )
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("ExternalDirectoryObjectId")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("ExternalDirectoryObjectId").alias(
                "external_directory_object_id"
            ),
            col("DisplayName").alias("display_name"),
            lower(trim(col("PrimarySmtpAddress"))).alias(
                "primary_smtp_address"
            ),
            col("Alias").alias("alias"),
            col("ManagedBy").alias("managed_by"),
            col("GroupMemberCount").alias("member_count"),
            col("GroupExternalMemberCount").alias("external_member_count"),
            col("SharePointSiteUrl").alias("sharepoint_site_url"),
            col("SharePointDocumentsUrl").alias("sharepoint_documents_url"),
            col("EmailAddresses").alias("email_addresses"),
            col("HiddenFromAddressListsEnabled").alias(
                "hidden_from_address_lists"
            ),
            col("AccessType").alias("access_type"),
            col("WhenCreated").alias("when_created"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="unified_groups",
    comment="Cleaned Exchange Online unified (M365) groups across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="unified_groups",
    source="v_unified_groups",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Entra Contacts (Organizational Contacts) ---


@dlt.view(name="v_entra_contacts")
def v_entra_contacts():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_contacts")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("displayName").alias("display_name"),
            col("givenName").alias("given_name"),
            col("surname"),
            lower(trim(col("mail"))).alias("mail"),
            col("jobTitle").alias("job_title"),
            col("department"),
            col("companyName").alias("company_name"),
            col("phones"),
            col("addresses"),
            col("proxyAddresses").alias("proxy_addresses"),
            col("onPremisesSyncEnabled").alias("on_premises_sync_enabled"),
            col("onPremisesLastSyncDateTime").alias("on_premises_last_sync"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="entra_contacts",
    comment="Cleaned organizational contacts across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="entra_contacts",
    source="v_entra_contacts",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- SharePoint Sites ---


@dlt.view(name="v_spo_sites")
def v_spo_sites():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.spo_sites")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("name"),
            col("displayName").alias("display_name"),
            col("webUrl").alias("web_url"),
            col("description"),
            col("createdDateTime").alias("created_at"),
            col("lastModifiedDateTime").alias("last_modified_at"),
            col("hostname"),
            col("isPersonalSite").alias("is_personal_site"),
            col("storageUsed").alias("storage_used"),
            col("storagePercentUsed").alias("storage_percent_used"),
            col("totalItemCount").alias("total_item_count"),
            col("listCount").alias("list_count"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="spo_sites",
    comment="Cleaned SharePoint sites with storage and item counts across all tenants",
    table_properties={"quality": "silver"},
)

dlt.apply_changes(
    target="spo_sites",
    source="v_spo_sites",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# ============================================================================
# Disabled entities — no landing data available
# ============================================================================
# entra_group_members (Phase 2 function error)
# exo_group_members (cmdlet not found)
# exo_mailbox_statistics (never ingested)
# onedrive_usage (never ingested)
