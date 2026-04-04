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
    df = spark.readStream.table("matoolkit_analytics.bronze.entra_users")

    # New columns may not exist in bronze yet — add as typed nulls
    _new_cols = {
        "mailNickname": "string",
        "streetAddress": "string",
        "postalCode": "string",
        "usageLocation": "string",
        "preferredLanguage": "string",
        "preferredDataLocation": "string",
        "businessPhones": "string",
        "mobilePhone": "string",
        "faxNumber": "string",
        "otherMails": "string",
        "employeeId": "string",
        "employeeType": "string",
        "employeeHireDate": "string",
        "employeeOrgData": "string",
        "creationType": "string",
        "lastPasswordChangeDateTime": "string",
        "passwordPolicies": "string",
        "securityIdentifier": "string",
        "externalUserState": "string",
        "externalUserStateChangeDateTime": "string",
        "identities": "string",
    }
    for c, t in _new_cols.items():
        if c not in df.columns:
            df = df.withColumn(c, lit(None).cast(t))

    return df.select(
        concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("id"),
        col("userPrincipalName").alias("user_principal_name"),
        lower(trim(col("mail"))).alias("mail"),
        col("displayName").alias("display_name"),
        col("givenName").alias("given_name"),
        col("surname"),
        col("mailNickname").alias("mail_nickname"),
        # --- Organization ---
        col("jobTitle").alias("job_title"),
        col("department"),
        col("officeLocation").alias("office_location"),
        col("city"),
        col("state"),
        col("country"),
        col("companyName").alias("company_name"),
        col("streetAddress").alias("street_address"),
        col("postalCode").alias("postal_code"),
        col("usageLocation").alias("usage_location"),
        col("preferredLanguage").alias("preferred_language"),
        col("preferredDataLocation").alias("preferred_data_location"),
        # --- Contact ---
        col("businessPhones").alias("business_phones"),
        col("mobilePhone").alias("mobile_phone"),
        col("faxNumber").alias("fax_number"),
        col("otherMails").alias("other_mails"),
        # --- Employee ---
        col("employeeId").alias("employee_id"),
        col("employeeType").alias("employee_type"),
        col("employeeHireDate").alias("employee_hire_date"),
        col("employeeOrgData.division").alias("employee_division"),
        col("employeeOrgData.costCenter").alias("employee_cost_center"),
        # --- Account status ---
        col("accountEnabled").alias("account_enabled"),
        col("userType").alias("user_type"),
        col("creationType").alias("creation_type"),
        col("createdDateTime").alias("created_at"),
        col("lastPasswordChangeDateTime").alias("last_password_change"),
        col("passwordPolicies").alias("password_policies"),
        col("securityIdentifier").alias("security_identifier"),
        # --- Guest / external ---
        col("externalUserState").alias("external_user_state"),
        col("externalUserStateChangeDateTime").alias(
            "external_user_state_changed_at"
        ),
        col("identities"),
        # --- Licensing ---
        col("proxyAddresses").alias("proxy_addresses"),
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
        # --- On-premises sync ---
        col("onPremisesSyncEnabled").alias("on_premises_sync_enabled"),
        col("onPremisesLastSyncDateTime").alias("on_premises_last_sync"),
        col("onPremisesDomainName").alias("on_premises_domain_name"),
        col("onPremisesDistinguishedName").alias("on_premises_distinguished_name"),
        col("onPremisesImmutableId").alias("on_premises_immutable_id"),
        col("onPremisesSamAccountName").alias("on_premises_sam_account_name"),
        col("onPremisesSecurityIdentifier").alias("on_premises_security_identifier"),
        col("onPremisesUserPrincipalName").alias("on_premises_user_principal_name"),
        # --- Extension attributes ---
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
        # --- Metadata ---
        col("_source_file"),
        col("_dlt_ingested_at"),
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
            col("resourceProvisioningOptions").alias("resource_provisioning_options"),
            expr("array_contains(resourceProvisioningOptions, 'Team')")
            .cast("boolean")
            .alias("is_teams_enabled"),
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


# Stream-static join: bronze.exo_mailboxes (core, streaming) LEFT JOIN
# bronze.exo_mailbox_statistics (enrichment, static) on ExchangeGuid = MailboxGuid.


@dlt.view(name="v_mailboxes")
def v_mailboxes():
    mbx = spark.readStream.table("matoolkit_analytics.bronze.exo_mailboxes")
    stats = spark.table("matoolkit_analytics.bronze.exo_mailbox_statistics")

    return mbx.join(
        stats,
        (mbx._tenant_key == stats._tenant_key)
        & (mbx.ExchangeGuid == stats.MailboxGuid),
        "left",
    ).select(
        concat_ws("_", mbx._tenant_key, mbx.ExchangeGuid).alias(
            "_scd_key"
        ),
        mbx._tenant_key.alias("tenant_key"),
        # --- Identity ---
        mbx.ExchangeGuid.alias("exchange_guid"),
        mbx.ExternalDirectoryObjectId.alias(
            "external_directory_object_id"
        ),
        mbx.UserPrincipalName.alias("user_principal_name"),
        lower(trim(mbx.PrimarySmtpAddress)).alias(
            "primary_smtp_address"
        ),
        mbx.DisplayName.alias("display_name"),
        mbx.Alias.alias("alias"),
        mbx.EmailAddresses.alias("email_addresses"),
        # --- Type & status ---
        mbx.Database.alias("database"),
        mbx.WhenCreated.alias("when_created"),
        mbx.WhenMailboxCreated.alias("when_mailbox_created"),
        mbx.IsMailboxEnabled.alias("is_mailbox_enabled"),
        mbx.HiddenFromAddressListsEnabled.alias(
            "hidden_from_address_lists"
        ),
        # --- Resource ---
        mbx.IsResource.alias("is_resource"),
        mbx.ResourceType.alias("resource_type"),
        mbx.ResourceCapacity.alias("resource_capacity"),
        mbx.RoomMailboxAccountEnabled.alias(
            "room_mailbox_account_enabled"
        ),
        # --- Forwarding ---
        mbx.ForwardingAddress.alias("forwarding_address"),
        mbx.ForwardingSmtpAddress.alias("forwarding_smtp_address"),
        mbx.DeliverToMailboxAndForward.alias(
            "deliver_to_mailbox_and_forward"
        ),
        # --- Delegation ---
        mbx.GrantSendOnBehalfTo.alias("grant_send_on_behalf_to"),
        mbx.MessageCopyForSendOnBehalfEnabled.alias(
            "message_copy_for_send_on_behalf"
        ),
        mbx.MessageCopyForSentAsEnabled.alias(
            "message_copy_for_sent_as"
        ),
        # --- Archive ---
        mbx.ArchiveStatus.alias("archive_status"),
        mbx.ArchiveState.alias("archive_state"),
        mbx.ArchiveGuid.alias("archive_guid"),
        mbx.ArchiveName.alias("archive_name"),
        mbx.AutoExpandingArchiveEnabled.alias(
            "auto_expanding_archive"
        ),
        # --- Compliance holds ---
        mbx.LitigationHoldEnabled.alias("litigation_hold_enabled"),
        mbx.LitigationHoldDate.alias("litigation_hold_date"),
        mbx.LitigationHoldOwner.alias("litigation_hold_owner"),
        mbx.LitigationHoldDuration.alias("litigation_hold_duration"),
        mbx.InPlaceHolds.alias("in_place_holds"),
        mbx.ComplianceTagHoldApplied.alias(
            "compliance_tag_hold_applied"
        ),
        mbx.DelayHoldApplied.alias("delay_hold_applied"),
        # --- Retention ---
        mbx.RetentionPolicy.alias("retention_policy"),
        mbx.RetentionHoldEnabled.alias("retention_hold_enabled"),
        mbx.RetainDeletedItemsFor.alias("retain_deleted_items_for"),
        mbx.SingleItemRecoveryEnabled.alias(
            "single_item_recovery_enabled"
        ),
        # --- Quotas ---
        mbx.IssueWarningQuota.alias("issue_warning_quota"),
        mbx.ProhibitSendQuota.alias("prohibit_send_quota"),
        mbx.ProhibitSendReceiveQuota.alias(
            "prohibit_send_receive_quota"
        ),
        mbx.UseDatabaseQuotaDefaults.alias(
            "use_database_quota_defaults"
        ),
        # --- Transport limits ---
        mbx.MaxReceiveSize.alias("max_receive_size"),
        mbx.MaxSendSize.alias("max_send_size"),
        mbx.RecipientLimits.alias("recipient_limits"),
        # --- Migration state ---
        mbx.MailboxMoveStatus.alias("mailbox_move_status"),
        mbx.MailboxMoveBatchName.alias("mailbox_move_batch_name"),
        mbx.MailboxMoveRemoteHostName.alias(
            "mailbox_move_remote_host_name"
        ),
        mbx.MailboxMoveFlags.alias("mailbox_move_flags"),
        # --- Soft-delete / inactive ---
        mbx.IsInactiveMailbox.alias("is_inactive_mailbox"),
        mbx.IsSoftDeletedByDisable.alias(
            "is_soft_deleted_by_disable"
        ),
        mbx.IsSoftDeletedByRemove.alias(
            "is_soft_deleted_by_remove"
        ),
        mbx.WhenSoftDeleted.alias("when_soft_deleted"),
        # --- Custom attributes ---
        mbx.CustomAttribute1.alias("custom_attribute_1"),
        mbx.CustomAttribute2.alias("custom_attribute_2"),
        mbx.CustomAttribute3.alias("custom_attribute_3"),
        mbx.CustomAttribute4.alias("custom_attribute_4"),
        mbx.CustomAttribute5.alias("custom_attribute_5"),
        mbx.CustomAttribute6.alias("custom_attribute_6"),
        mbx.CustomAttribute7.alias("custom_attribute_7"),
        mbx.CustomAttribute8.alias("custom_attribute_8"),
        mbx.CustomAttribute9.alias("custom_attribute_9"),
        mbx.CustomAttribute10.alias("custom_attribute_10"),
        mbx.CustomAttribute11.alias("custom_attribute_11"),
        mbx.CustomAttribute12.alias("custom_attribute_12"),
        mbx.CustomAttribute13.alias("custom_attribute_13"),
        mbx.CustomAttribute14.alias("custom_attribute_14"),
        mbx.CustomAttribute15.alias("custom_attribute_15"),
        mbx.ExtensionCustomAttribute1.alias(
            "extension_custom_attribute_1"
        ),
        mbx.ExtensionCustomAttribute2.alias(
            "extension_custom_attribute_2"
        ),
        mbx.ExtensionCustomAttribute3.alias(
            "extension_custom_attribute_3"
        ),
        mbx.ExtensionCustomAttribute4.alias(
            "extension_custom_attribute_4"
        ),
        mbx.ExtensionCustomAttribute5.alias(
            "extension_custom_attribute_5"
        ),
        # --- Statistics (from enrichment) ---
        stats.ItemCount.cast("long").alias("item_count"),
        stats.TablesTotalSize.cast("long").alias(
            "total_item_size_bytes"
        ),
        stats.DeletedItemCount.cast("long").alias("deleted_item_count"),
        stats.LastLogonTime.alias("last_logon_time"),
        stats.LastLoggedOnUserAccount.alias("last_logon_user"),
        stats.IsArchiveMailbox.alias("is_archive_mailbox"),
        # --- Metadata ---
        mbx._source_file,
        mbx._dlt_ingested_at,
    )


dlt.create_streaming_table(
    name="mailboxes",
    comment="Exchange Online mailboxes with statistics across all tenants",
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
# Stream-static join: bronze.spo_sites (Phase 1 enumeration, streaming) LEFT JOIN
# bronze.spo_site_usage (Phase 2 enrichment, static) to produce a single unified table.


@dlt.view(name="v_spo_sites")
def v_spo_sites():
    sites = spark.readStream.table("matoolkit_analytics.bronze.spo_sites")
    details = spark.table("matoolkit_analytics.bronze.spo_site_usage")

    # Add new enrichment columns as typed nulls if they don't exist yet in
    # bronze (Phase 2 may not have written data with these fields yet).
    _new_cols = {
        "storageQuota": "long",
        "webTemplate": "string",
        "isModern": "boolean",
        "groupId": "string",
        "isGroupConnected": "boolean",
        "isTeamsConnected": "boolean",
        "hubSiteId": "string",
        "isHubSite": "boolean",
        "owner": "string",
        "readOnly": "boolean",
        "language": "long",
    }
    for c, t in _new_cols.items():
        if c not in details.columns:
            details = details.withColumn(c, lit(None).cast(t))

    return sites.join(
        details,
        (sites._tenant_key == details._tenant_key)
        & (sites.webUrl == details.siteUrl),
        "left",
    ).select(
        # Phase 1 — Graph getAllSites
        concat_ws("_", sites._tenant_key, sites.id).alias("_scd_key"),
        sites._tenant_key.alias("tenant_key"),
        sites.id,
        sites.name,
        sites.displayName.alias("display_name"),
        sites.webUrl.alias("web_url"),
        sites.description,
        sites.createdDateTime.alias("created_at"),
        sites.lastModifiedDateTime.alias("last_modified_at"),
        sites.hostname,
        sites.isPersonalSite.alias("is_personal_site"),
        # Phase 2 — PnP enrichment: usage
        details.storageUsed.alias("storage_used"),
        details.storagePercentUsed.alias("storage_percent_used"),
        details.storageQuota.cast("long").alias("storage_quota"),
        details.totalItemCount.alias("total_item_count"),
        details.listCount.alias("list_count"),
        # Phase 2 — PnP enrichment: template & generation
        details.webTemplate.alias("web_template"),
        details.isModern.alias("is_modern"),
        # Phase 2 — PnP enrichment: group & Teams
        details.groupId.alias("group_id"),
        details.isGroupConnected.alias("is_group_connected"),
        details.isTeamsConnected.alias("is_teams_connected"),
        # Phase 2 — PnP enrichment: hub site
        details.hubSiteId.alias("hub_site_id"),
        details.isHubSite.alias("is_hub_site"),
        # Phase 2 — PnP enrichment: owner, lock state, language
        details.owner,
        details.readOnly.alias("read_only"),
        details.language,
        sites._source_file,
        sites._dlt_ingested_at,
    )


dlt.create_streaming_table(
    name="spo_sites",
    comment="SharePoint sites with usage, template, group, hub, and assessment metadata across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="spo_sites",
    source="v_spo_sites",
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


# --- Entra Group Owners ---


@dlt.view(name="v_entra_group_owners")
def v_entra_group_owners():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_group_owners")
        .select(
            concat_ws("_", col("_tenant_key"), col("groupId"), col("id")).alias(
                "_scd_key"
            ),
            col("_tenant_key").alias("tenant_key"),
            col("groupId").alias("group_id"),
            col("id").alias("owner_id"),
            col("displayName").alias("display_name"),
            col("userPrincipalName").alias("user_principal_name"),
            lower(trim(col("mail"))).alias("mail"),
            col("`@odata.type`").alias("owner_type"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="entra_group_owners",
    comment="Cleaned Entra group ownerships across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="entra_group_owners",
    source="v_entra_group_owners",
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


# ============================================================================
# Teams
# ============================================================================


# --- Teams (from team settings Phase 2) ---


@dlt.view(name="v_teams")
def v_teams():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.teams_team_settings")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("displayName").alias("display_name"),
            col("description"),
            col("isArchived").alias("is_archived"),
            col("webUrl").alias("web_url"),
            col("classification"),
            col("specialization"),
            col("visibility"),
            col("funSettings").alias("fun_settings"),
            col("messagingSettings").alias("messaging_settings"),
            col("memberSettings").alias("member_settings"),
            col("guestSettings").alias("guest_settings"),
            col("discoverySettings").alias("discovery_settings"),
            col("summary"),
            col("createdDateTime").alias("created_at"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="teams",
    comment="Cleaned and deduplicated Teams across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="teams",
    source="v_teams",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Teams Channels ---


@dlt.view(name="v_teams_channels")
def v_teams_channels():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.teams_channels")
        .select(
            concat_ws("_", col("_tenant_key"), col("teamId"), col("id")).alias(
                "_scd_key"
            ),
            col("_tenant_key").alias("tenant_key"),
            col("teamId").alias("team_id"),
            col("id"),
            col("displayName").alias("display_name"),
            col("description"),
            col("membershipType").alias("membership_type"),
            col("webUrl").alias("web_url"),
            col("email"),
            col("isArchived").alias("is_archived"),
            col("createdDateTime").alias("created_at"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="teams_channels",
    comment="Cleaned and deduplicated Teams channels across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="teams_channels",
    source="v_teams_channels",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Teams Channel Members ---


@dlt.view(name="v_teams_channel_members")
def v_teams_channel_members():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.teams_channel_members")
        .select(
            concat_ws(
                "_",
                col("_tenant_key"),
                col("teamId"),
                col("channelId"),
                col("id"),
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("teamId").alias("team_id"),
            col("channelId").alias("channel_id"),
            col("id"),
            col("displayName").alias("display_name"),
            col("email"),
            col("roles"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="teams_channel_members",
    comment="Cleaned and deduplicated Teams private/shared channel members across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="teams_channel_members",
    source="v_teams_channel_members",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Teams Installed Apps ---


@dlt.view(name="v_teams_installed_apps")
def v_teams_installed_apps():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.teams_installed_apps")
        .select(
            concat_ws("_", col("_tenant_key"), col("teamId"), col("appId")).alias(
                "_scd_key"
            ),
            col("_tenant_key").alias("tenant_key"),
            col("teamId").alias("team_id"),
            col("appId").alias("app_id"),
            col("displayName").alias("app_display_name"),
            col("teamsAppId").alias("teams_app_id"),
            col("version"),
            col("publishingState").alias("publishing_state"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="teams_installed_apps",
    comment="Cleaned and deduplicated Teams installed apps across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="teams_installed_apps",
    source="v_teams_installed_apps",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Teams Channel Tabs ---


@dlt.view(name="v_teams_channel_tabs")
def v_teams_channel_tabs():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.teams_channel_tabs")
        .select(
            concat_ws(
                "_",
                col("_tenant_key"),
                col("teamId"),
                col("channelId"),
                col("id"),
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("teamId").alias("team_id"),
            col("channelId").alias("channel_id"),
            col("id"),
            col("displayName").alias("display_name"),
            col("webUrl").alias("web_url"),
            col("appDisplayName").alias("app_display_name"),
            col("teamsAppId").alias("teams_app_id"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="teams_channel_tabs",
    comment="Cleaned and deduplicated Teams channel tabs across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="teams_channel_tabs",
    source="v_teams_channel_tabs",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# ============================================================================
# Devices
# ============================================================================


# --- Entra ID Devices ---


@dlt.view(name="v_devices")
def v_devices():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_devices")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("deviceId").alias("device_id"),
            col("displayName").alias("display_name"),
            col("operatingSystem").alias("operating_system"),
            col("operatingSystemVersion").alias("operating_system_version"),
            col("trustType").alias("trust_type"),
            col("isManaged").alias("is_managed"),
            col("isCompliant").alias("is_compliant"),
            col("accountEnabled").alias("account_enabled"),
            col("approximateLastSignInDateTime").alias(
                "approximate_last_sign_in"
            ),
            col("createdDateTime").alias("created_at"),
            col("model"),
            col("manufacturer"),
            col("profileType").alias("profile_type"),
            col("deviceCategory").alias("device_category"),
            col("enrollmentProfileName").alias("enrollment_profile_name"),
            col("onPremisesSyncEnabled").alias("on_premises_sync_enabled"),
            col("onPremisesLastSyncDateTime").alias(
                "on_premises_last_sync"
            ),
            col("onPremisesSecurityIdentifier").alias(
                "on_premises_security_identifier"
            ),
            col("mdmAppId").alias("mdm_app_id"),
            col("registrationDateTime").alias("registration_date_time"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="devices",
    comment="Cleaned and deduplicated Entra ID devices across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="devices",
    source="v_devices",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Intune Managed Devices ---


@dlt.view(name="v_intune_managed_devices")
def v_intune_managed_devices():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.intune_managed_devices"
        )
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("deviceName").alias("device_name"),
            col("managedDeviceOwnerType").alias("managed_device_owner_type"),
            col("enrolledDateTime").alias("enrolled_at"),
            col("lastSyncDateTime").alias("last_sync_at"),
            col("operatingSystem").alias("operating_system"),
            col("complianceState").alias("compliance_state"),
            col("jailBroken").alias("jail_broken"),
            col("managementAgent").alias("management_agent"),
            col("osVersion").alias("os_version"),
            col("azureADRegistered").alias("azure_ad_registered"),
            col("deviceEnrollmentType").alias("device_enrollment_type"),
            col("emailAddress").alias("email_address"),
            col("azureADDeviceId").alias("azure_ad_device_id"),
            col("deviceRegistrationState").alias(
                "device_registration_state"
            ),
            col("isEncrypted").alias("is_encrypted"),
            col("userPrincipalName").alias("user_principal_name"),
            col("model"),
            col("manufacturer"),
            col("serialNumber").alias("serial_number"),
            col("userId").alias("user_id"),
            col("userDisplayName").alias("user_display_name"),
            col("totalStorageSpaceInBytes")
            .cast("long")
            .alias("total_storage_space_bytes"),
            col("freeStorageSpaceInBytes")
            .cast("long")
            .alias("free_storage_space_bytes"),
            col("managedDeviceName").alias("managed_device_name"),
            col("partnerReportedThreatState").alias(
                "partner_reported_threat_state"
            ),
            col("autopilotEnrolled").alias("autopilot_enrolled"),
            col("isSupervised").alias("is_supervised"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="intune_managed_devices",
    comment="Cleaned and deduplicated Intune managed devices across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="intune_managed_devices",
    source="v_intune_managed_devices",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Microsoft Defender for Endpoint Devices ---


@dlt.view(name="v_mde_devices")
def v_mde_devices():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.mde_devices")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("computerDnsName").alias("computer_dns_name"),
            col("firstSeen").alias("first_seen"),
            col("lastSeen").alias("last_seen"),
            col("osPlatform").alias("os_platform"),
            col("osArchitecture").alias("os_architecture"),
            col("version").alias("os_version"),
            col("osBuild").cast("long").alias("os_build"),
            col("lastIpAddress").alias("last_ip_address"),
            col("lastExternalIpAddress").alias(
                "last_external_ip_address"
            ),
            col("healthStatus").alias("health_status"),
            col("onboardingStatus").alias("onboarding_status"),
            col("riskScore").alias("risk_score"),
            col("exposureLevel").alias("exposure_level"),
            col("aadDeviceId").alias("aad_device_id"),
            col("machineTags").alias("machine_tags"),
            col("rbacGroupName").alias("rbac_group_name"),
            col("rbacGroupId").alias("rbac_group_id"),
            col("deviceValue").alias("device_value"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="mde_devices",
    comment="Cleaned and deduplicated MDE devices across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="mde_devices",
    source="v_mde_devices",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# ============================================================================
# Entra Applications
# ============================================================================


# --- Applications (App Registrations) ---


@dlt.view(name="v_applications")
def v_applications():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_applications")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("appId").alias("app_id"),
            col("displayName").alias("display_name"),
            col("description"),
            col("signInAudience").alias("sign_in_audience"),
            col("identifierUris").alias("identifier_uris"),
            col("appRoles").alias("app_roles"),
            col("requiredResourceAccess").alias("required_resource_access"),
            col("keyCredentials").alias("key_credentials"),
            col("passwordCredentials").alias("password_credentials"),
            col("web"),
            col("spa"),
            col("publicClient").alias("public_client"),
            col("api"),
            col("optionalClaims").alias("optional_claims"),
            col("groupMembershipClaims").alias("group_membership_claims"),
            col("tags"),
            col("applicationTemplateId").alias("application_template_id"),
            col("createdDateTime").alias("created_at"),
            col("publisherDomain").alias("publisher_domain"),
            col("verifiedPublisher").alias("verified_publisher"),
            col("info"),
            col("notes"),
            col("servicePrincipalLockConfiguration").alias(
                "service_principal_lock_configuration"
            ),
            col("isFallbackPublicClient").alias("is_fallback_public_client"),
            col("tokenEncryptionKeyId").alias("token_encryption_key_id"),
            col("certification"),
            col("samlMetadataUrl").alias("saml_metadata_url"),
            col("disabledByMicrosoftStatus").alias("disabled_by_microsoft_status"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="applications",
    comment="Cleaned Entra app registrations across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="applications",
    source="v_applications",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Service Principals (Enterprise Apps) ---


@dlt.view(name="v_service_principals")
def v_service_principals():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_service_principals")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("appId").alias("app_id"),
            col("appDisplayName").alias("app_display_name"),
            col("displayName").alias("display_name"),
            col("description"),
            col("servicePrincipalType").alias("service_principal_type"),
            col("appOwnerOrganizationId").alias("app_owner_organization_id"),
            col("accountEnabled").alias("account_enabled"),
            col("appRoleAssignmentRequired").alias("app_role_assignment_required"),
            col("appRoles").alias("app_roles"),
            col("oauth2PermissionScopes").alias("oauth2_permission_scopes"),
            col("tags"),
            col("servicePrincipalNames").alias("service_principal_names"),
            col("homepage"),
            col("loginUrl").alias("login_url"),
            col("logoutUrl").alias("logout_url"),
            col("replyUrls").alias("reply_urls"),
            col("keyCredentials").alias("key_credentials"),
            col("passwordCredentials").alias("password_credentials"),
            col("preferredSingleSignOnMode").alias("preferred_sso_mode"),
            col("samlSingleSignOnSettings").alias("saml_sso_settings"),
            col("signInAudience").alias("sign_in_audience"),
            col("notes"),
            col("notificationEmailAddresses").alias("notification_email_addresses"),
            col("info"),
            col("applicationTemplateId").alias("application_template_id"),
            col("verifiedPublisher").alias("verified_publisher"),
            col("alternativeNames").alias("alternative_names"),
            col("tokenEncryptionKeyId").alias("token_encryption_key_id"),
            col("resourceSpecificApplicationPermissions").alias(
                "resource_specific_application_permissions"
            ),
            col("disabledByMicrosoftStatus").alias("disabled_by_microsoft_status"),
            col("preferredTokenSigningKeyThumbprint").alias(
                "preferred_token_signing_key_thumbprint"
            ),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="service_principals",
    comment="Cleaned Entra service principals (enterprise apps) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="service_principals",
    source="v_service_principals",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Delegated Permission Grants ---


@dlt.view(name="v_delegated_permission_grants")
def v_delegated_permission_grants():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.entra_delegated_permission_grants"
        )
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("clientId").alias("client_id"),
            col("consentType").alias("consent_type"),
            col("principalId").alias("principal_id"),
            col("resourceId").alias("resource_id"),
            col("scope"),
            (col("consentType") == "AllPrincipals").alias("is_admin_consent"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="delegated_permission_grants",
    comment="Cleaned delegated permission grants across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="delegated_permission_grants",
    source="v_delegated_permission_grants",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- SP Assignments (User/Group/SP assignments to enterprise apps) ---


@dlt.view(name="v_sp_assignments")
def v_sp_assignments():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.entra_sp_assignments"
        )
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("servicePrincipalId"), col("id")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("servicePrincipalId").alias("service_principal_id"),
            col("id"),
            col("appRoleId").alias("app_role_id"),
            col("principalDisplayName").alias("principal_display_name"),
            col("principalId").alias("principal_id"),
            col("principalType").alias("principal_type"),
            col("resourceDisplayName").alias("resource_display_name"),
            col("resourceId").alias("resource_id"),
            col("createdDateTime").alias("created_at"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="sp_assignments",
    comment="Cleaned SP assignments (users/groups/SPs assigned to enterprise apps) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="sp_assignments",
    source="v_sp_assignments",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Service Principal Owners ---


@dlt.view(name="v_sp_owners")
def v_sp_owners():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_sp_owners")
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("servicePrincipalId"), col("id")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("servicePrincipalId").alias("service_principal_id"),
            col("id").alias("owner_id"),
            col("displayName").alias("display_name"),
            col("userPrincipalName").alias("user_principal_name"),
            lower(trim(col("mail"))).alias("mail"),
            col("`@odata.type`").alias("owner_type"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="sp_owners",
    comment="Cleaned service principal ownerships across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="sp_owners",
    source="v_sp_owners",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Application Registration Owners ---


@dlt.view(name="v_app_owners")
def v_app_owners():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_app_owners")
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("applicationId"), col("id")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("applicationId").alias("application_id"),
            col("id").alias("owner_id"),
            col("displayName").alias("display_name"),
            col("userPrincipalName").alias("user_principal_name"),
            lower(trim(col("mail"))).alias("mail"),
            col("`@odata.type`").alias("owner_type"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="app_owners",
    comment="Cleaned application registration ownerships across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="app_owners",
    source="v_app_owners",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Application Permission Grants (Application permissions granted to SPs) ---


@dlt.view(name="v_application_permission_grants")
def v_application_permission_grants():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.entra_application_permission_grants"
        )
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("servicePrincipalId"), col("id")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("servicePrincipalId").alias("service_principal_id"),
            col("id"),
            col("appRoleId").alias("app_role_id"),
            col("principalDisplayName").alias("principal_display_name"),
            col("principalId").alias("principal_id"),
            col("principalType").alias("principal_type"),
            col("resourceDisplayName").alias("resource_display_name"),
            col("resourceId").alias("resource_id"),
            col("createdDateTime").alias("created_at"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="application_permission_grants",
    comment="Cleaned application permission grants (app-only permissions) across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="application_permission_grants",
    source="v_application_permission_grants",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- SP Claims Mapping Policies ---


@dlt.view(name="v_sp_claims_mapping_policies")
def v_sp_claims_mapping_policies():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.entra_sp_claims_mapping_policies"
        )
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("servicePrincipalId"), col("id")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("servicePrincipalId").alias("service_principal_id"),
            col("id"),
            col("displayName").alias("display_name"),
            col("definition"),
            col("isOrganizationDefault").alias("is_organization_default"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="sp_claims_mapping_policies",
    comment="Cleaned claims mapping policies per service principal across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="sp_claims_mapping_policies",
    source="v_sp_claims_mapping_policies",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Delegated Permission Classifications ---


@dlt.view(name="v_delegated_permission_classifications")
def v_delegated_permission_classifications():
    return (
        spark.readStream.table(
            "matoolkit_analytics.bronze.entra_delegated_permission_classifications"
        )
        .select(
            concat_ws(
                "_", col("_tenant_key"), col("servicePrincipalId"), col("id")
            ).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("servicePrincipalId").alias("service_principal_id"),
            col("id"),
            col("permissionId").alias("permission_id"),
            col("permissionName").alias("permission_name"),
            col("classification"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="delegated_permission_classifications",
    comment="Cleaned delegated permission classifications across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="delegated_permission_classifications",
    source="v_delegated_permission_classifications",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Sign-In Logs ---


@dlt.view(name="v_sign_in_logs")
def v_sign_in_logs():
    return (
        spark.readStream.table("matoolkit_analytics.bronze.entra_sign_in_logs")
        .select(
            concat_ws("_", col("_tenant_key"), col("id")).alias("_scd_key"),
            col("_tenant_key").alias("tenant_key"),
            col("id"),
            col("createdDateTime").alias("created_at"),
            col("appDisplayName").alias("app_display_name"),
            col("appId").alias("app_id"),
            col("ipAddress").alias("ip_address"),
            col("clientAppUsed").alias("client_app_used"),
            col("conditionalAccessStatus").alias("conditional_access_status"),
            col("isInteractive").alias("is_interactive"),
            col("resourceDisplayName").alias("resource_display_name"),
            col("resourceId").alias("resource_id"),
            col("riskDetail").alias("risk_detail"),
            col("riskLevelAggregated").alias("risk_level_aggregated"),
            col("riskLevelDuringSignIn").alias("risk_level_during_sign_in"),
            col("riskState").alias("risk_state"),
            col("riskEventTypes_v2").alias("risk_event_types"),
            col("userDisplayName").alias("user_display_name"),
            col("userId").alias("user_id"),
            col("userPrincipalName").alias("user_principal_name"),
            # Flatten nested structs
            col("status.errorCode").alias("error_code"),
            col("status.failureReason").alias("failure_reason"),
            col("location.city").alias("location_city"),
            col("location.state").alias("location_state"),
            col("location.countryOrRegion").alias("location_country"),
            col("deviceDetail.operatingSystem").alias("device_os"),
            col("deviceDetail.browser").alias("device_browser"),
            col("deviceDetail.deviceId").alias("device_id"),
            col("_source_file"),
            col("_dlt_ingested_at"),
        )
    )


dlt.create_streaming_table(
    name="sign_in_logs",
    comment="Cleaned Entra sign-in logs across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="sign_in_logs",
    source="v_sign_in_logs",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- App Proxy Configuration ---


@dlt.view(name="v_app_proxy_config")
def v_app_proxy_config():
    # onPremisesPublishing struct only exists when data is present (not in
    # fallback schema). Use col().try*() pattern is not available, so select
    # base columns always and flatten the struct only when it exists.
    df = spark.readStream.table("matoolkit_analytics.bronze.entra_app_proxy_config")
    base = df.select(
        concat_ws("_", col("_tenant_key"), col("applicationId")).alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("applicationId").alias("application_id"),
        col("id"),
        col("displayName").alias("display_name"),
        col("_source_file"),
        col("_dlt_ingested_at"),
    )
    if "onPremisesPublishing" in df.columns:
        base = (
            df.select(
                concat_ws("_", col("_tenant_key"), col("applicationId")).alias(
                    "_scd_key"
                ),
                col("_tenant_key").alias("tenant_key"),
                col("applicationId").alias("application_id"),
                col("id"),
                col("displayName").alias("display_name"),
                col("onPremisesPublishing.externalUrl").alias("external_url"),
                col("onPremisesPublishing.internalUrl").alias("internal_url"),
                col("onPremisesPublishing.externalAuthenticationType").alias(
                    "external_auth_type"
                ),
                col("onPremisesPublishing.isTranslateHostHeaderEnabled").alias(
                    "is_translate_host_header_enabled"
                ),
                col("onPremisesPublishing.isTranslateLinksInBodyEnabled").alias(
                    "is_translate_links_in_body_enabled"
                ),
                col("onPremisesPublishing.isHttpOnlyCookieEnabled").alias(
                    "is_http_only_cookie_enabled"
                ),
                col("onPremisesPublishing.isSecureCookieEnabled").alias(
                    "is_secure_cookie_enabled"
                ),
                col("onPremisesPublishing.isPersistentCookieEnabled").alias(
                    "is_persistent_cookie_enabled"
                ),
                col("_source_file"),
                col("_dlt_ingested_at"),
            )
        )
    return base


dlt.create_streaming_table(
    name="app_proxy_config",
    comment="Cleaned Application Proxy configuration across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="app_proxy_config",
    source="v_app_proxy_config",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)


# --- Provisioning Jobs ---


@dlt.view(name="v_provisioning_jobs")
def v_provisioning_jobs():
    # schedule and status structs only exist when data is present (not in
    # fallback schema). Select base columns and add structs conditionally.
    df = spark.readStream.table("matoolkit_analytics.bronze.entra_provisioning_jobs")
    cols = [
        concat_ws(
            "_", col("_tenant_key"), col("servicePrincipalId"), col("id")
        ).alias("_scd_key"),
        col("_tenant_key").alias("tenant_key"),
        col("servicePrincipalId").alias("service_principal_id"),
        col("id"),
        col("templateId").alias("template_id"),
    ]
    if "schedule" in df.columns:
        cols.append(col("schedule"))
    if "status" in df.columns:
        cols.append(col("status"))
    cols.extend([col("_source_file"), col("_dlt_ingested_at")])
    return df.select(*cols)


dlt.create_streaming_table(
    name="provisioning_jobs",
    comment="Cleaned provisioning jobs across all tenants",
    table_properties=SILVER_TABLE_PROPERTIES,
)

dlt.apply_changes(
    target="provisioning_jobs",
    source="v_provisioning_jobs",
    keys=["_scd_key"],
    sequence_by=col("_dlt_ingested_at"),
    stored_as_scd_type=1,
)
