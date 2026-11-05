-- 0056 · Material sources and capability periods (#72, D-089): material connections to a REST or SOAP service or a PostgreSQL database beside
-- OData. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.material_connection DROP CONSTRAINT IF EXISTS material_connection_kind_check;
ALTER TABLE core.material_connection ALTER COLUMN base_url DROP NOT NULL;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS next_link_path text;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS request_body text;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS soap_action text;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS query text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'material_connection_kind_ck') THEN
        ALTER TABLE core.material_connection ADD CONSTRAINT material_connection_kind_ck CHECK (
            kind IN ('odata','rest','soap','database')
            AND (kind = 'database') = (base_url IS NULL)
            AND (kind = 'database') = (query IS NOT NULL)
            AND (kind = 'soap') = (request_body IS NOT NULL));
    END IF;
END $$;
