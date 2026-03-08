import dlt
from pyspark.sql.functions import col, lit, current_timestamp, regexp_extract

STORAGE_ACCOUNT = spark.conf.get("analytics.storage_account_name")
LANDING_CONTAINER = "landing"
BASE_PATH = f"abfss://{LANDING_CONTAINER}@{STORAGE_ACCOUNT}.dfs.core.windows.net"


def _read_landing(schedule_tier, entity_type):
    """Read JSONL files from the landing container using Auto Loader."""
    landing_path = f"{BASE_PATH}/{schedule_tier}/{entity_type}/*/"
    return (
        spark.readStream
        .format("cloudFiles")
        .option("cloudFiles.format", "json")
        .option("cloudFiles.schemaLocation", f"{BASE_PATH}/_schemas/{entity_type}")
        .option("cloudFiles.inferColumnTypes", "true")
        .option("cloudFiles.schemaEvolutionMode", "addNewColumns")
        .option("multiLine", "false")
        .option("cloudFiles.useIncrementalListing", "auto")
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


# --- Core tier entities ---

@dlt.table(
    name="entra_users",
    comment="Raw entra_users data from all tenants",
    table_properties={"quality": "bronze", "pipelines.autoOptimize.managed": "true", "delta.columnMapping.mode": "name"},
)
@dlt.expect("valid_record", "id IS NOT NULL OR ExternalDirectoryObjectId IS NOT NULL")
def entra_users():
    return _read_landing("core", "entra_users")


@dlt.table(
    name="entra_groups",
    comment="Raw entra_groups data from all tenants",
    table_properties={"quality": "bronze", "pipelines.autoOptimize.managed": "true", "delta.columnMapping.mode": "name"},
)
@dlt.expect("valid_record", "id IS NOT NULL OR ExternalDirectoryObjectId IS NOT NULL")
def entra_groups():
    return _read_landing("core", "entra_groups")
