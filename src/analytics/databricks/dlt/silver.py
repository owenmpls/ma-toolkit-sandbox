import dlt
from pyspark.sql.functions import (
    array_contains,
    col,
    concat_ws,
    explode,
    expr,
    from_json,
    lit,
    lower,
    size,
    trim,
    transform,
    when,
)
from pyspark.sql.types import (
    ArrayType,
    BooleanType,
    StringType,
    StructField,
    StructType,
)

# Delta retention — silver uses SCD Type 1 (update-in-place), so deleted file
# retention controls how far back TIMESTAMP AS OF works after VACUUM.
DELTA_DELETED_FILE_RETENTION = "interval 7 days"

SILVER_TABLE_PROPERTIES = {
    "quality": "silver",
    "delta.deletedFileRetentionDuration": DELTA_DELETED_FILE_RETENTION,
}

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
            # --- On-premises attributes ---
            col("onPremisesDomainName").alias("on_premises_domain_name"),
            col("onPremisesDistinguishedName").alias("on_premises_distinguished_name"),
            col("onPremisesImmutableId").alias("on_premises_immutable_id"),
            col("onPremisesSamAccountName").alias("on_premises_sam_account_name"),
            col("onPremisesSecurityIdentifier").alias("on_premises_security_identifier"),
            col("onPremisesUserPrincipalName").alias("on_premises_user_principal_name"),
            # --- Extension attributes (from onPremisesExtensionAttributes) ---
            col("onPremisesExtensionAttributes.extensionAttribute1").alias("extension_attribute_1"),
            col("onPremisesExtensionAttributes.extensionAttribute2").alias("extension_attribute_2"),
            col("onPremisesExtensionAttributes.extensionAttribute3").alias("extension_attribute_3"),
            col("onPremisesExtensionAttributes.extensionAttribute4").alias("extension_attribute_4"),
            col("onPremisesExtensionAttributes.extensionAttribute5").alias("extension_attribute_5"),
            col("onPremisesExtensionAttributes.extensionAttribute6").alias("extension_attribute_6"),
            col("onPremisesExtensionAttributes.extensionAttribute7").alias("extension_attribute_7"),
            col("onPremisesExtensionAttributes.extensionAttribute8").alias("extension_attribute_8"),
            col("onPremisesExtensionAttributes.extensionAttribute9").alias("extension_attribute_9"),
            col("onPremisesExtensionAttributes.extensionAttribute10").alias("extension_attribute_10"),
            col("onPremisesExtensionAttributes.extensionAttribute11").alias("extension_attribute_11"),
            col("onPremisesExtensionAttributes.extensionAttribute12").alias("extension_attribute_12"),
            col("onPremisesExtensionAttributes.extensionAttribute13").alias("extension_attribute_13"),
            col("onPremisesExtensionAttributes.extensionAttribute14").alias("extension_attribute_14"),
            col("onPremisesExtensionAttributes.extensionAttribute15").alias("extension_attribute_15"),
            # --- Provisioning errors ---
            col("onPremisesProvisioningErrors").alias("on_premises_provisioning_errors"),
            col("serviceProvisioningErrors").alias("service_provisioning_errors"),
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
    table_properties=SILVER_TABLE_PROPERTIES,
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
            # --- On-premises attributes ---
            col("onPremisesDomainName").alias("on_premises_domain_name"),
            col("onPremisesNetBiosName").alias("on_premises_netbios_name"),
            col("onPremisesSamAccountName").alias("on_premises_sam_account_name"),
            col("onPremisesSecurityIdentifier").alias("on_premises_security_identifier"),
            # --- Provisioning errors ---
            col("onPremisesProvisioningErrors").alias("on_premises_provisioning_errors"),
            col("serviceProvisioningErrors").alias("service_provisioning_errors"),
            col("createdDateTime").alias("created_at"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="groups",
    comment="Cleaned and deduplicated Entra groups across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
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
    table_properties=SILVER_TABLE_PROPERTIES,
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
            col("Alias").alias("alias"),
            col("ExternalEmailAddress").alias("external_email_address"),
            col("EmailAddresses").alias("email_addresses"),
            col("HiddenFromAddressListsEnabled").alias(
                "hidden_from_address_lists"
            ),
            col("FirstName").alias("first_name"),
            col("LastName").alias("last_name"),
            col("Company").alias("company"),
            col("Department").alias("department"),
            col("Title").alias("title"),
            col("Office").alias("office"),
            col("City").alias("city"),
            col("Manager").alias("manager"),
            col("WhenCreated").alias("when_created"),
            col("WhenChanged").alias("when_changed"),
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
    name="exo_contacts",
    comment="Cleaned Exchange Online mail contacts across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="exo_contacts",
    source="v_exo_contacts",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- EXO Mail Users ---


@dlt.view(name="v_exo_mail_users")
def v_exo_mail_users():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.exo_mail_users")
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
            col("ExternalEmailAddress").alias("external_email_address"),
            col("EmailAddresses").alias("email_addresses"),
            col("HiddenFromAddressListsEnabled").alias(
                "hidden_from_address_lists"
            ),
            col("FirstName").alias("first_name"),
            col("LastName").alias("last_name"),
            col("Company").alias("company"),
            col("Department").alias("department"),
            col("Title").alias("title"),
            col("Office").alias("office"),
            col("City").alias("city"),
            col("Manager").alias("manager"),
            col("WhenCreated").alias("when_created"),
            col("WhenChanged").alias("when_changed"),
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
    name="exo_mail_users",
    comment="Cleaned Exchange Online mail users across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="exo_mail_users",
    source="v_exo_mail_users",
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
    table_properties=SILVER_TABLE_PROPERTIES,
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
    table_properties=SILVER_TABLE_PROPERTIES,
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
            # --- Provisioning errors ---
            col("onPremisesProvisioningErrors").alias("on_premises_provisioning_errors"),
            col("serviceProvisioningErrors").alias("service_provisioning_errors"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="entra_contacts",
    comment="Cleaned organizational contacts across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
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
    df = spark.readStream.table("matoolkit_analytics.bronze.spo_sites")

    return df.select(
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
        col("_source_file"),
        col("_dlt_ingested_at"),
    )


dlt.create_streaming_table(
    name="spo_sites",
    comment="Cleaned SharePoint sites (metadata only) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="spo_sites",
    source="v_spo_sites",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- SharePoint Site Usage ---


@dlt.view(name="v_spo_site_usage")
def v_spo_site_usage():
    df = spark.readStream.table("matoolkit_analytics.bronze.spo_site_usage")

    return df.select(
        concat_ws("_", col("_tenant_key"), col("siteUrl")).alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("siteUrl").alias("site_url"),
        col("storageUsed").alias("storage_used"),
        col("storagePercentUsed").alias("storage_percent_used"),
        col("totalItemCount").alias("total_item_count"),
        col("listCount").alias("list_count"),
        col("_source_file"),
        col("_dlt_ingested_at"),
    )


dlt.create_streaming_table(
    name="spo_site_usage",
    comment="SharePoint site storage and item counts across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="spo_site_usage",
    source="v_spo_site_usage",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Entra Group Members ---


@dlt.view(name="v_entra_group_members")
def v_entra_group_members():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_group_members")
        .select(
            concat_ws("_", col("_tenant_key"), col("groupId"), col("id")).alias(
                "_scd_key"
            ),
            col("_tenant_key").alias("tenant_key"),
            col("groupId").alias("group_id"),
            col("id").alias("member_id"),
            col("displayName").alias("display_name"),
            col("userPrincipalName").alias("user_principal_name"),
            lower(trim(col("mail"))).alias("mail"),
            col("`@odata.type`").alias("member_type"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="entra_group_members",
    comment="Cleaned Entra group memberships across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="entra_group_members",
    source="v_entra_group_members",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- EXO Group Members ---


@dlt.view(name="v_exo_group_members")
def v_exo_group_members():
    df = spark.readStream.table("matoolkit_analytics.bronze.exo_group_members")

    # groupObjectId/memberObjectId added after initial ingestion — handle missing columns
    for col_name in ("groupObjectId", "memberObjectId"):
        if col_name not in df.columns:
            df = df.withColumn(col_name, lit(None).cast("string"))

    return df.select(
        # Use object IDs for SCD key when available, fall back to name-based key
        when(
            col("groupObjectId").isNotNull() & col("memberObjectId").isNotNull(),
            concat_ws("_", col("_tenant_key"), col("groupObjectId"), col("memberObjectId")),
        )
        .otherwise(
            concat_ws("_", col("_tenant_key"), col("groupIdentity"), col("memberName"))
        )
        .alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("groupIdentity").alias("group_identity"),
        col("groupObjectId").alias("group_object_id"),
        col("groupType").alias("group_type"),
        col("memberName").alias("member_name"),
        col("memberObjectId").alias("member_object_id"),
        col("memberType").alias("member_type"),
        lower(trim(col("primarySmtp"))).alias("primary_smtp_address"),
        col("_source_file"),
        col("_dlt_ingested_at"),
    )


dlt.create_streaming_table(
    name="exo_group_members",
    comment="Cleaned Exchange Online group memberships across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="exo_group_members",
    source="v_exo_group_members",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- EXO Mailbox Statistics ---


@dlt.view(name="v_exo_mailbox_statistics")
def v_exo_mailbox_statistics():
    df = spark.readStream.table("matoolkit_analytics.bronze.exo_mailbox_statistics")

    # TotalItemSize / TotalDeletedItemSize land as struct<IsUnlimited:boolean>
    # when PowerShell serialises ByteQuantifiedSize incorrectly (Value is an
    # empty object).  Use TablesTotalSize (bigint, always populated) as the
    # best available total-size proxy until the enrichment container ships
    # numeric byte counts.
    return df.select(
        concat_ws("_", col("_tenant_key"), col("MailboxGuid")).alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("MailboxGuid").alias("mailbox_guid"),
        col("DisplayName").alias("display_name"),
        col("ItemCount").cast("long").alias("item_count"),
        col("TablesTotalSize").cast("long").alias("total_item_size_bytes"),
        col("DeletedItemCount").cast("long").alias("deleted_item_count"),
        lit(None).cast("long").alias("total_deleted_item_size_bytes"),
        col("LastLogonTime").alias("last_logon_time"),
        col("LastLoggedOnUserAccount").alias("last_logon_user"),
        col("IsArchiveMailbox").alias("is_archive_mailbox"),
        col("_source_file"),
        col("_dlt_ingested_at"),
    )


dlt.create_streaming_table(
    name="exo_mailbox_statistics",
    comment="Exchange Online mailbox statistics (size, item counts) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="exo_mailbox_statistics",
    source="v_exo_mailbox_statistics",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- SPO Site Permissions ---
# Auto Loader may infer arrays as array<string> when early records had empty arrays.
# These schemas let us parse JSON strings back to structs in the silver layer.

_PRINCIPAL_SCHEMA = StructType(
    [
        StructField("loginName", StringType()),
        StructField("title", StringType()),
        StructField("email", StringType()),
        StructField("isEEEU", BooleanType()),
        StructField("isGuest", BooleanType()),
    ]
)

_ROLE_ASSIGNMENT_SCHEMA = StructType(
    [
        StructField("principalType", StringType()),
        StructField("principalId", StringType()),
        StructField("principalName", StringType()),
        StructField("loginName", StringType()),
        StructField("roleDefinitions", ArrayType(StringType())),
    ]
)

_SHARING_LINK_SCHEMA = StructType(
    [
        StructField("library", StringType()),
        StructField("itemPath", StringType()),
        StructField("itemType", StringType()),
        StructField("linkId", StringType()),
        StructField("linkUrl", StringType()),
        StructField("scope", StringType()),
        StructField("type", StringType()),
        StructField("hasPassword", BooleanType()),
        StructField("expirationDateTime", StringType()),
    ]
)


def _parse_json_array(df, column_name, struct_schema):
    """If a column is array<string> (JSON strings), parse each element to struct."""
    element_type = df.schema[column_name].dataType.elementType
    if isinstance(element_type, StringType):
        return transform(col(column_name), lambda x: from_json(x, struct_schema))
    return col(column_name)


# --- SPO Site Permissions Summary ---


@dlt.view(name="v_spo_site_permissions_summary")
def v_spo_site_permissions_summary():
    df = spark.readStream.table("matoolkit_analytics.bronze.spo_site_permissions")

    # sensitivityLabel may land as struct (PnP object) or string depending on
    # entity version. Handle both: extract DisplayName from struct, pass string through.
    label_col = df.schema["sensitivityLabel"].dataType
    if hasattr(label_col, "fieldNames") and "DisplayName" in label_col.fieldNames():
        sensitivity_expr = col("sensitivityLabel.DisplayName")
    else:
        sensitivity_expr = col("sensitivityLabel")

    return df.select(
        concat_ws("_", col("_tenant_key"), col("siteUrl")).alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("siteUrl").alias("site_url"),
        col("sharingCapability").alias("sharing_capability"),
        sensitivity_expr.alias("sensitivity_label"),
        col("hasUniqueRoleAssignments").alias("has_unique_role_assignments"),
        col("hasEEEU").alias("has_eeeu"),
        col("hasGuests").alias("has_guests"),
        col("hasOrgWideLinks").alias("has_org_wide_links"),
        col("hasAnonymousLinks").alias("has_anonymous_links"),
        size(col("admins")).alias("admin_count"),
        size(col("groups")).alias("group_count"),
        size(col("sharingLinks")).alias("sharing_link_count"),
        size(col("documentLibraries")).alias("library_count"),
        col("_source_file"),
        col("_dlt_ingested_at"),
    )


dlt.create_streaming_table(
    name="spo_site_permissions_summary",
    comment="Per-site permissions summary flags (EEEU, guests, sharing) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="spo_site_permissions_summary",
    source="v_spo_site_permissions_summary",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- SPO Site Permission Principals ---


@dlt.view(name="v_spo_site_permission_principals")
def v_spo_site_permission_principals():
    df = spark.readStream.table("matoolkit_analytics.bronze.spo_site_permissions")

    # Parse admins from JSON strings if Auto Loader inferred array<string>
    admins_col = _parse_json_array(df, "admins", _PRINCIPAL_SCHEMA)

    # Admins: explode admins array, role = "admin"
    admins_df = (
        df.withColumn("_admins_parsed", admins_col)
        .select(
            col("_tenant_key"),
            col("siteUrl"),
            explode(col("_admins_parsed")).alias("principal"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
        .select(
            concat_ws(
                "_",
                col("_tenant_key"),
                col("siteUrl"),
                lit("admin"),
                col("principal.loginName"),
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("siteUrl").alias("site_url"),
            lit("admin").alias("role"),
            lit(None).cast("string").alias("group_title"),
            col("principal.loginName").alias("login_name"),
            col("principal.title").alias("title"),
            col("principal.email").alias("email"),
            col("principal.isEEEU").alias("is_eeeu"),
            col("principal.isGuest").alias("is_guest"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )

    # Group members: explode groups, then explode members
    members_df = (
        df.select(
            col("_tenant_key"),
            col("siteUrl"),
            explode(col("groups")).alias("grp"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
        .select(
            col("_tenant_key"),
            col("siteUrl"),
            col("grp.title").alias("group_title"),
            explode(col("grp.members")).alias("member"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
        .select(
            concat_ws(
                "_",
                col("_tenant_key"),
                col("siteUrl"),
                lit("member"),
                col("member.loginName"),
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("siteUrl").alias("site_url"),
            lit("member").alias("role"),
            col("group_title"),
            col("member.loginName").alias("login_name"),
            col("member.title").alias("title"),
            col("member.email").alias("email"),
            col("member.isEEEU").alias("is_eeeu"),
            col("member.isGuest").alias("is_guest"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )

    return admins_df.unionByName(members_df)


dlt.create_streaming_table(
    name="spo_site_permission_principals",
    comment="Per-principal permissions (admins + group members) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="spo_site_permission_principals",
    source="v_spo_site_permission_principals",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- SPO Sharing Links ---


@dlt.view(name="v_spo_sharing_links")
def v_spo_sharing_links():
    df = spark.readStream.table("matoolkit_analytics.bronze.spo_site_permissions")

    # Parse sharing links from JSON strings if Auto Loader inferred array<string>
    links_col = _parse_json_array(df, "sharingLinks", _SHARING_LINK_SCHEMA)

    return (
        df.withColumn("_links_parsed", links_col)
        .select(
            col("_tenant_key"),
            col("siteUrl"),
            explode(col("_links_parsed")).alias("link"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("siteUrl"), col("link.linkId")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("siteUrl").alias("site_url"),
            col("link.library").alias("library"),
            col("link.itemPath").alias("item_path"),
            col("link.itemType").alias("item_type"),
            col("link.linkId").alias("link_id"),
            col("link.linkUrl").alias("link_url"),
            col("link.scope").alias("scope"),
            col("link.type").alias("type"),
            col("link.hasPassword").alias("has_password"),
            col("link.expirationDateTime").alias("expiration_date_time"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="spo_sharing_links",
    comment="Sharing link inventory per site/library across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="spo_sharing_links",
    source="v_spo_sharing_links",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- EXO Mailbox Permissions ---
# Auto Loader may infer arrays as array<string> when early records had empty arrays.
# These schemas let us parse JSON strings back to structs in the silver layer.

_MAILBOX_PERM_SCHEMA = StructType(
    [
        StructField("trustee", StringType()),
        StructField("accessRights", ArrayType(StringType())),
        StructField("isInherited", BooleanType()),
        StructField("deny", BooleanType()),
    ]
)

_SEND_AS_PERM_SCHEMA = StructType(
    [
        StructField("trustee", StringType()),
        StructField("accessRights", ArrayType(StringType())),
        StructField("isInherited", BooleanType()),
    ]
)


# --- EXO Mailbox Permissions Summary ---


@dlt.view(name="v_exo_mailbox_permissions_summary")
def v_exo_mailbox_permissions_summary():
    df = spark.readStream.table("matoolkit_analytics.bronze.exo_mailbox_permissions")

    return df.select(
        concat_ws("_", col("_tenant_key"), col("exchangeGuid")).alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("exchangeGuid").alias("exchange_guid"),
        col("userPrincipalName").alias("user_principal_name"),
        col("primarySmtpAddress").alias("primary_smtp_address"),
        col("displayName").alias("display_name"),
        col("recipientTypeDetails").alias("recipient_type_details"),
        col("hasDelegates").alias("has_delegates"),
        col("hasFullAccess").alias("has_full_access"),
        col("hasSendAs").alias("has_send_as"),
        col("hasSendOnBehalf").alias("has_send_on_behalf"),
        size(col("mailboxPermissions")).alias("mailbox_permission_count"),
        size(col("sendAsPermissions")).alias("send_as_permission_count"),
        size(col("sendOnBehalfTo")).alias("send_on_behalf_count"),
        col("permissionCount").alias("total_permission_count"),
        col("_source_file"),
        col("_dlt_ingested_at"),
    )


dlt.create_streaming_table(
    name="exo_mailbox_permissions_summary",
    comment="Per-mailbox permission summary flags (delegates, FullAccess, SendAs) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="exo_mailbox_permissions_summary",
    source="v_exo_mailbox_permissions_summary",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- EXO Mailbox Permission Entries ---


@dlt.view(name="v_exo_mailbox_permission_entries")
def v_exo_mailbox_permission_entries():
    df = spark.readStream.table("matoolkit_analytics.bronze.exo_mailbox_permissions")

    # Parse mailbox permissions from JSON strings if Auto Loader inferred array<string>
    mbx_perms_col = _parse_json_array(df, "mailboxPermissions", _MAILBOX_PERM_SCHEMA)

    # MailboxPermission entries (FullAccess, ReadPermission, etc.)
    mbx_perm_df = (
        df.withColumn("_perms_parsed", mbx_perms_col)
        .select(
            col("_tenant_key"),
            col("exchangeGuid"),
            col("userPrincipalName"),
            col("primarySmtpAddress"),
            col("recipientTypeDetails"),
            explode(col("_perms_parsed")).alias("perm"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
        .select(
            concat_ws(
                "_",
                col("_tenant_key"),
                col("exchangeGuid"),
                lit("mailbox"),
                col("perm.trustee"),
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("exchangeGuid").alias("exchange_guid"),
            col("userPrincipalName").alias("user_principal_name"),
            col("primarySmtpAddress").alias("primary_smtp_address"),
            col("recipientTypeDetails").alias("recipient_type_details"),
            col("perm.trustee").alias("trustee"),
            concat_ws(",", col("perm.accessRights")).alias("access_rights"),
            lit("MailboxPermission").alias("permission_type"),
            col("perm.isInherited").alias("is_inherited"),
            col("perm.deny").alias("deny"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )

    # Parse SendAs permissions from JSON strings if Auto Loader inferred array<string>
    sendas_col = _parse_json_array(df, "sendAsPermissions", _SEND_AS_PERM_SCHEMA)

    # SendAs entries
    sendas_df = (
        df.withColumn("_sendas_parsed", sendas_col)
        .select(
            col("_tenant_key"),
            col("exchangeGuid"),
            col("userPrincipalName"),
            col("primarySmtpAddress"),
            col("recipientTypeDetails"),
            explode(col("_sendas_parsed")).alias("perm"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
        .select(
            concat_ws(
                "_",
                col("_tenant_key"),
                col("exchangeGuid"),
                lit("sendas"),
                col("perm.trustee"),
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("exchangeGuid").alias("exchange_guid"),
            col("userPrincipalName").alias("user_principal_name"),
            col("primarySmtpAddress").alias("primary_smtp_address"),
            col("recipientTypeDetails").alias("recipient_type_details"),
            col("perm.trustee").alias("trustee"),
            concat_ws(",", col("perm.accessRights")).alias("access_rights"),
            lit("SendAs").alias("permission_type"),
            col("perm.isInherited").alias("is_inherited"),
            lit(None).cast("boolean").alias("deny"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )

    # SendOnBehalf entries (simple string array of DNs)
    sendonbehalf_df = (
        df.select(
            col("_tenant_key"),
            col("exchangeGuid"),
            col("userPrincipalName"),
            col("primarySmtpAddress"),
            col("recipientTypeDetails"),
            explode(col("sendOnBehalfTo")).alias("trustee_dn"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
        .select(
            concat_ws(
                "_",
                col("_tenant_key"),
                col("exchangeGuid"),
                lit("sendonbehalf"),
                col("trustee_dn"),
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("exchangeGuid").alias("exchange_guid"),
            col("userPrincipalName").alias("user_principal_name"),
            col("primarySmtpAddress").alias("primary_smtp_address"),
            col("recipientTypeDetails").alias("recipient_type_details"),
            col("trustee_dn").alias("trustee"),
            lit("SendOnBehalf").alias("access_rights"),
            lit("SendOnBehalf").alias("permission_type"),
            lit(False).alias("is_inherited"),
            lit(None).cast("boolean").alias("deny"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )

    return mbx_perm_df.unionByName(sendas_df).unionByName(sendonbehalf_df)


dlt.create_streaming_table(
    name="exo_mailbox_permission_entries",
    comment="Per-trustee permission entries (FullAccess, SendAs, SendOnBehalf) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="exo_mailbox_permission_entries",
    source="v_exo_mailbox_permission_entries",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)
