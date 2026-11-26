-- =====================================================================
-- Pulaski — reporting role privileges (#132, D-129)
-- Target: PostgreSQL 18. Load after schema-draft.sql (or the migrations). Re-runnable.
--
-- pulaski_report is a NOLOGIN group role that may read the report schema's views and nothing else: no core, ts, evt or sec table, and no
-- writes. Views run with their owner's rights, so the views can read what they need. A DBA creates each BI tool's own login:
--   CREATE ROLE powerbi LOGIN PASSWORD '...' CONNECTION LIMIT 5;
--   GRANT pulaski_report TO powerbi;
--   GRANT CONNECT ON DATABASE pulaski TO powerbi;
--   ALTER ROLE powerbi SET statement_timeout = '5min';             -- per login: a group role's settings don't pass to its members
--   ALTER ROLE powerbi SET default_transaction_read_only = on;
-- No password is kept here. See design/reporting.md.
-- =====================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_report') THEN
        CREATE ROLE pulaski_report NOLOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA report TO pulaski_report;
GRANT SELECT ON ALL TABLES IN SCHEMA report TO pulaski_report;
ALTER DEFAULT PRIVILEGES IN SCHEMA report GRANT SELECT ON TABLES TO pulaski_report;
