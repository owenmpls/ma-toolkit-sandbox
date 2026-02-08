-- =============================================================================
-- M&A Toolkit Scheduler — SQL Schema
-- Target: Azure SQL Database (serverless)
-- =============================================================================

-- Versioned runbook storage
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

-- Automation toggle per runbook
CREATE TABLE runbook_automation_settings (
    runbook_name        NVARCHAR(128) NOT NULL PRIMARY KEY,
    automation_enabled  BIT NOT NULL DEFAULT 0,
    enabled_at          DATETIME2,
    enabled_by          NVARCHAR(256),
    disabled_at         DATETIME2,
    disabled_by         NVARCHAR(256)
);

-- Batch tracking (unique per runbook + execution time)
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

-- Member tracking for change detection
CREATE TABLE batch_members (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    batch_id                INT NOT NULL REFERENCES batches(id),
    member_key              NVARCHAR(256) NOT NULL,
    data_json               NVARCHAR(MAX),
    status                  NVARCHAR(32) NOT NULL DEFAULT 'active',
    added_at                DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    removed_at              DATETIME2,
    failed_at               DATETIME2,
    add_dispatched_at       DATETIME2,
    remove_dispatched_at    DATETIME2,
    CONSTRAINT UQ_batch_member UNIQUE (batch_id, member_key),
    CONSTRAINT CK_member_status CHECK (status IN ('active', 'removed', 'failed'))
);

-- Phase execution tracking (one per batch + phase + runbook version)
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

-- Per-member step execution tracking
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

-- Batch init step execution tracking
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

