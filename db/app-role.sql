-- =====================================================================
-- Pulaski — application role privileges                     2026-10-12
-- Target: PostgreSQL 18. Load after schema-draft.sql. Re-runnable. [D-012]
--
-- pulaski_app is a NOLOGIN group role holding what the API needs. A
-- deployment creates its own login and grants membership:
--   CREATE ROLE pulaski_api LOGIN PASSWORD '...';
--   GRANT pulaski_app TO pulaski_api;
--
-- The application reads with parameterized SELECTs and writes with
-- parameterized INSERT/UPDATE inside one transaction per command. It
-- never deletes: corrections are new events, and retired master data is
-- deactivated. evt.event is INSERT and SELECT only (D-004), backed by the
-- append-only trigger in schema-draft.sql.
--
-- Grant ts and sec tables here as the features that use them are built.
-- =====================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        CREATE ROLE pulaski_app NOLOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA core, evt, ts TO pulaski_app;

GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA core TO pulaski_app;

GRANT SELECT, INSERT ON evt.event TO pulaski_app;
REVOKE UPDATE, DELETE, TRUNCATE ON evt.event FROM pulaski_app;
GRANT USAGE ON SEQUENCE evt.event_position_seq TO pulaski_app;

-- History the application appends to (D-010). Like evt.event, never updated or deleted.
GRANT SELECT, INSERT ON ts.job_status_change TO pulaski_app;
GRANT SELECT, INSERT ON ts.lot_event TO pulaski_app;

-- Partition maintenance and the schema version check at API startup (D-028). Guarded, so this
-- file still runs on a database that db/migrate.sh hasn't brought up to date yet.
DO $$
BEGIN
    IF to_regprocedure('ts.ensure_partitions(integer)') IS NOT NULL THEN
        GRANT EXECUTE ON FUNCTION ts.ensure_partitions(integer) TO pulaski_app;
    END IF;
    IF to_regclass('public.schema_migration') IS NOT NULL THEN
        GRANT SELECT ON public.schema_migration TO pulaski_app;
    END IF;
END $$;
