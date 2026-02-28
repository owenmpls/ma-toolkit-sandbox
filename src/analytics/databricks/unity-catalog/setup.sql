-- Unity Catalog setup for M&A Toolkit Analytics
-- Run this in a Databricks SQL warehouse or notebook with catalog admin privileges

-- Create the analytics catalog
CREATE CATALOG IF NOT EXISTS matoolkit_analytics
COMMENT 'M&A Toolkit analytics data - tenant migration data across Bronze and Silver layers';

-- Create Bronze schema (raw ingested data)
CREATE SCHEMA IF NOT EXISTS matoolkit_analytics.bronze
COMMENT 'Raw data from Microsoft tenant APIs - Auto Loader streaming tables';

-- Create Silver schema (cleaned, deduplicated data)
CREATE SCHEMA IF NOT EXISTS matoolkit_analytics.silver
COMMENT 'Cleaned and deduplicated data - SCD Type 1 transformations';

-- Grant usage to data engineering group (adjust group name as needed)
-- GRANT USE CATALOG ON CATALOG matoolkit_analytics TO `data-engineering`;
-- GRANT USE SCHEMA ON SCHEMA matoolkit_analytics.bronze TO `data-engineering`;
-- GRANT USE SCHEMA ON SCHEMA matoolkit_analytics.silver TO `data-engineering`;
-- GRANT SELECT ON SCHEMA matoolkit_analytics.silver TO `data-analysts`;
