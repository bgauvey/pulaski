-- 0023 · People from HR and ERP sources (D-054): an imported person keeps the system and key it is matched on, and the
-- source's version. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

ALTER TABLE core.person ADD COLUMN IF NOT EXISTS external_system text;
ALTER TABLE core.person ADD COLUMN IF NOT EXISTS external_key text;
ALTER TABLE core.person ADD COLUMN IF NOT EXISTS external_version text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'person_external_ck' AND conrelid = 'core.person'::regclass) THEN
        ALTER TABLE core.person ADD CONSTRAINT person_external_ck
            CHECK ((origin = 'local') = (external_key IS NULL) AND (external_system IS NULL) = (external_key IS NULL));
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS person_external_uq ON core.person (external_system, external_key) WHERE external_key IS NOT NULL;
