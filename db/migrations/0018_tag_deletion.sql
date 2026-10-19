-- 0018 · Tag deletion (D-042): tag bindings may be physically removed after their quality source references are cleared.
-- The application role receives DELETE on this table only. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT DELETE ON TABLE core.equipment_tag TO pulaski_app;
    END IF;
END $$;
