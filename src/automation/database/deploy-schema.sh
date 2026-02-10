#!/bin/bash
set -euo pipefail

# ---------------------------------------------------------------------------
# deploy-schema.sh — SQL migration runner for Azure deploymentScript
#
# Runs inside an ACI container within the VNet. Installs sqlcmd, connects to
# Azure SQL via Entra ID managed identity, and applies numbered migrations
# from the migrations/ directory (embedded inline via heredocs).
# ---------------------------------------------------------------------------

echo "=== SQL Schema Deployment ==="
echo "Server:   $SQL_SERVER"
echo "Database: $SQL_DATABASE"
echo "Identity: $IDENTITY_CLIENT_ID"

# ---------------------------------------------------------------------------
# Install sqlcmd (mssql-tools18)
# ---------------------------------------------------------------------------
echo "--- Installing sqlcmd ---"
curl -sL https://packages.microsoft.com/keys/microsoft.asc | apt-key add - 2>/dev/null
curl -sL https://packages.microsoft.com/config/ubuntu/22.04/prod.list > /etc/apt/sources.list.d/mssql-release.list
apt-get update -qq && ACCEPT_EULA=Y apt-get install -y -qq mssql-tools18 unixodbc-dev > /dev/null
export PATH="$PATH:/opt/mssql-tools18/bin"

echo "sqlcmd version: $(sqlcmd --version 2>&1 | head -1)"

# Common sqlcmd flags: managed identity auth, trust server cert for private endpoint
SQLCMD_BASE="sqlcmd -S $SQL_SERVER -d $SQL_DATABASE --authentication-method=ActiveDirectoryManagedIdentity -U $IDENTITY_CLIENT_ID -C"

# Helper: run a SQL command
run_sql() {
    $SQLCMD_BASE -Q "$1"
}

# Helper: run SQL and return a single scalar value (trimmed)
run_sql_scalar() {
    $SQLCMD_BASE -h -1 -W -Q "$1" | head -1 | tr -d '[:space:]'
}

# ---------------------------------------------------------------------------
# Bootstrap migration tracking table
# ---------------------------------------------------------------------------
echo "--- Bootstrapping __schema_migrations table ---"
run_sql "
IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = '__schema_migrations' AND type = 'U')
CREATE TABLE __schema_migrations (
    id INT IDENTITY(1,1) PRIMARY KEY,
    migration NVARCHAR(256) NOT NULL UNIQUE,
    applied_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
"

# ---------------------------------------------------------------------------
# Migration definitions — each entry is: migration_name + SQL content
# New migrations are appended here as heredocs.
# ---------------------------------------------------------------------------
declare -a MIGRATION_NAMES
declare -a MIGRATION_SQLS

# --- 001_initial_schema ---
MIGRATION_NAMES+=("001_initial_schema")
read -r -d '' SQL_001 << 'MIGRATION_EOF' || true
IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = 'runbooks' AND type = 'U')
CREATE TABLE runbooks (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    name                    NVARCHAR(128) NOT NULL,
    version                 INT NOT NULL,
    yaml_content            NVARCHAR(MAX) NOT NULL,
    data_table_name         NVARCHAR(128) NOT NULL,
    is_active               BIT NOT NULL DEFAULT 1,
    overdue_behavior        NVARCHAR(16) NOT NULL DEFAULT 'rerun',
    ignore_overdue_applied  BIT NOT NULL DEFAULT 0,
    rerun_init              BIT NOT NULL DEFAULT 0,
    created_at              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    last_error              NVARCHAR(MAX)   NULL,
    last_error_at           DATETIME2       NULL,
    CONSTRAINT UQ_runbook_name_version UNIQUE (name, version),
    CONSTRAINT CK_overdue_behavior CHECK (overdue_behavior IN ('rerun', 'ignore'))
);

IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = 'runbook_automation_settings' AND type = 'U')
CREATE TABLE runbook_automation_settings (
    runbook_name        NVARCHAR(128) NOT NULL PRIMARY KEY,
    automation_enabled  BIT NOT NULL DEFAULT 0,
    enabled_at          DATETIME2,
    enabled_by          NVARCHAR(256),
    disabled_at         DATETIME2,
    disabled_by         NVARCHAR(256)
);

IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = 'batches' AND type = 'U')
CREATE TABLE batches (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    runbook_id          INT NOT NULL REFERENCES runbooks(id),
    batch_start_time    DATETIME2,
    status              NVARCHAR(32) NOT NULL DEFAULT 'detected',
    detected_at         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    init_dispatched_at  DATETIME2,
    is_manual           BIT NOT NULL DEFAULT 0,
    created_by          NVARCHAR(256),
    current_phase       NVARCHAR(128),
    CONSTRAINT UQ_batch UNIQUE (runbook_id, batch_start_time),
    CONSTRAINT CK_batch_status CHECK (status IN (
        'detected', 'init_dispatched', 'active', 'completed', 'failed'))
);

IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = 'batch_members' AND type = 'U')
CREATE TABLE batch_members (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    batch_id                INT NOT NULL REFERENCES batches(id),
    member_key              NVARCHAR(256) NOT NULL,
    data_json               NVARCHAR(MAX),
    worker_data_json        NVARCHAR(MAX),
    status                  NVARCHAR(32) NOT NULL DEFAULT 'active',
    added_at                DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    removed_at              DATETIME2,
    failed_at               DATETIME2,
    add_dispatched_at       DATETIME2,
    remove_dispatched_at    DATETIME2,
    CONSTRAINT UQ_batch_member UNIQUE (batch_id, member_key),
    CONSTRAINT CK_member_status CHECK (status IN ('active', 'removed', 'failed'))
);

IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = 'phase_executions' AND type = 'U')
CREATE TABLE phase_executions (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    batch_id            INT NOT NULL REFERENCES batches(id),
    phase_name          NVARCHAR(128) NOT NULL,
    offset_minutes      INT NOT NULL,
    due_at              DATETIME2 NOT NULL,
    runbook_version     INT NOT NULL,
    status              NVARCHAR(32) NOT NULL DEFAULT 'pending',
    dispatched_at       DATETIME2,
    completed_at        DATETIME2,
    CONSTRAINT UQ_phase_exec UNIQUE (batch_id, phase_name, runbook_version),
    CONSTRAINT CK_phase_status CHECK (status IN (
        'pending', 'dispatched', 'completed', 'failed', 'skipped', 'superseded'))
);

IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = 'step_executions' AND type = 'U')
CREATE TABLE step_executions (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    phase_execution_id  INT NOT NULL REFERENCES phase_executions(id),
    batch_member_id     INT NOT NULL REFERENCES batch_members(id),
    step_name           NVARCHAR(128) NOT NULL,
    step_index          INT NOT NULL,
    worker_id           NVARCHAR(64),
    function_name       NVARCHAR(128),
    params_json         NVARCHAR(MAX),
    on_failure          NVARCHAR(256),
    status              NVARCHAR(32) NOT NULL DEFAULT 'pending',
    job_id              NVARCHAR(128),
    result_json         NVARCHAR(MAX),
    error_message       NVARCHAR(MAX),
    dispatched_at       DATETIME2,
    completed_at        DATETIME2,
    is_poll_step        BIT NOT NULL DEFAULT 0,
    poll_interval_sec   INT,
    poll_timeout_sec    INT,
    poll_started_at     DATETIME2,
    last_polled_at      DATETIME2,
    poll_count          INT NOT NULL DEFAULT 0,
    retry_count         INT NOT NULL DEFAULT 0,
    max_retries         INT,
    retry_interval_sec  INT,
    retry_after         DATETIME2,
    CONSTRAINT UQ_step_exec UNIQUE (phase_execution_id, batch_member_id, step_name),
    CONSTRAINT CK_step_status CHECK (status IN (
        'pending', 'dispatched', 'succeeded', 'failed',
        'polling', 'poll_timeout', 'cancelled', 'rolled_back', 'skipped'))
);

IF NOT EXISTS (SELECT * FROM sys.objects WHERE name = 'init_executions' AND type = 'U')
CREATE TABLE init_executions (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    batch_id            INT NOT NULL REFERENCES batches(id),
    step_name           NVARCHAR(128) NOT NULL,
    step_index          INT NOT NULL,
    runbook_version     INT NOT NULL,
    worker_id           NVARCHAR(64),
    function_name       NVARCHAR(128),
    params_json         NVARCHAR(MAX),
    on_failure          NVARCHAR(256),
    status              NVARCHAR(32) NOT NULL DEFAULT 'pending',
    job_id              NVARCHAR(128),
    result_json         NVARCHAR(MAX),
    error_message       NVARCHAR(MAX),
    dispatched_at       DATETIME2,
    completed_at        DATETIME2,
    is_poll_step        BIT NOT NULL DEFAULT 0,
    poll_interval_sec   INT,
    poll_timeout_sec    INT,
    poll_started_at     DATETIME2,
    last_polled_at      DATETIME2,
    poll_count          INT NOT NULL DEFAULT 0,
    retry_count         INT NOT NULL DEFAULT 0,
    max_retries         INT,
    retry_interval_sec  INT,
    retry_after         DATETIME2,
    CONSTRAINT UQ_init_exec UNIQUE (batch_id, step_name, runbook_version),
    CONSTRAINT CK_init_status CHECK (status IN (
        'pending', 'dispatched', 'succeeded', 'failed', 'polling', 'poll_timeout'))
);
MIGRATION_EOF
MIGRATION_SQLS+=("$SQL_001")

# ---------------------------------------------------------------------------
# Apply migrations in order
# ---------------------------------------------------------------------------
applied=0
skipped=0

for i in "${!MIGRATION_NAMES[@]}"; do
    name="${MIGRATION_NAMES[$i]}"
    sql="${MIGRATION_SQLS[$i]}"

    already_applied=$(run_sql_scalar "SELECT COUNT(*) FROM __schema_migrations WHERE migration = '$name';")

    if [ "$already_applied" != "0" ]; then
        echo "SKIP: $name (already applied)"
        skipped=$((skipped + 1))
        continue
    fi

    echo "APPLY: $name"
    $SQLCMD_BASE -Q "$sql"

    run_sql "INSERT INTO __schema_migrations (migration) VALUES ('$name');"
    echo "  -> recorded in __schema_migrations"
    applied=$((applied + 1))
done

echo ""
echo "=== Done: $applied applied, $skipped skipped ==="
