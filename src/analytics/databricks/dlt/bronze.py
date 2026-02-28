import dlt
from pyspark.sql.functions import col, lit, input_file_name, current_timestamp, regexp_extract

STORAGE_ACCOUNT = spark.conf.get("analytics.storage_account_name")
LANDING_CONTAINER = "landing"
BASE_PATH = f"abfss://{LANDING_CONTAINER}@{STORAGE_ACCOUNT}.dfs.core.windows.net"


def create_bronze_table(entity_type: str, schedule_tier: str, source_system: str):
    """Factory function to create a bronze streaming table for any entity type.

    Reads JSONL files from the landing container using Auto Loader,
    adds metadata columns, and stores as a Delta streaming table.
    """

    @dlt.table(
        name=entity_type,
        comment=f"Raw {entity_type} data from all tenants",
        schema="matoolkit_analytics.bronze",
        table_properties={
            "quality": "bronze",
            "pipelines.autoOptimize.managed": "true",
            "delta.columnMapping.mode": "name",
        },
    )
    @dlt.expect("valid_record", "id IS NOT NULL OR ExternalDirectoryObjectId IS NOT NULL")
    def bronze_table():
        # Wildcard * reads across all tenant_key folders
        landing_path = f"{BASE_PATH}/{schedule_tier}/{entity_type}/*/"

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
            .withColumn(
                "_tenant_key",
                regexp_extract(input_file_name(), r"/([^/]+)/\d{4}-\d{2}-\d{2}/", 1),
            )
            .withColumn("_source_file", input_file_name())
            .withColumn("_source_system", lit(source_system))
            .withColumn("_schedule_tier", lit(schedule_tier))
            .withColumn("_dlt_ingested_at", current_timestamp())
        )

    return bronze_table


# --- Core tier entities ---
create_bronze_table("entra_users", "core", "graph_api")
create_bronze_table("entra_groups", "core", "graph_api")
create_bronze_table("entra_contacts", "core", "graph_api")
create_bronze_table("exo_mailboxes", "core", "exchange_online")
create_bronze_table("exo_contacts", "core", "exchange_online")
create_bronze_table("exo_distribution_groups", "core", "exchange_online")
create_bronze_table("exo_unified_groups", "core", "exchange_online")
create_bronze_table("spo_sites", "core", "sharepoint_online")

# --- Core enrichment tier entities ---
create_bronze_table("entra_group_members", "core_enrichment", "graph_api")
create_bronze_table("exo_group_members", "core_enrichment", "exchange_online")
