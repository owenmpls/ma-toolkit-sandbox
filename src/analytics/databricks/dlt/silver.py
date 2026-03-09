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


# ============================================================================
# Pending entities — bronze tables exist but silver views need schema
# validation against actual bronze data before activation. The EXO silver
# views reference specific column names that may not match the auto-inferred
# bronze schema from Get-EXOMailbox / Get-MailContact / etc. output.
#
# To activate: inspect bronze table schemas in Databricks, adjust column
# references in the silver views to match, then uncomment.
# ============================================================================

# Disabled: entra_contacts, entra_group_members (no landing data)
# Disabled: spo_sites (no landing data)
# Disabled: exo_group_members, exo_mailbox_statistics, onedrive_usage (no landing data)
# Pending schema validation: mailboxes, exo_contacts, distribution_groups, unified_groups
