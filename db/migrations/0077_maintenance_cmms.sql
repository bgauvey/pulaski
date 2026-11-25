-- 0077 · Maintenance requests in a CMMS (#127c, D-115): the feed a request was taken on through and the CMMS's own number for it.
-- Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.maintenance_request ADD COLUMN IF NOT EXISTS external_system text;       -- the delivery feed's code
ALTER TABLE core.maintenance_request ADD COLUMN IF NOT EXISTS external_reference text;    -- the CMMS's work order number

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'maintenance_request_external_ck') THEN
        ALTER TABLE core.maintenance_request ADD CONSTRAINT maintenance_request_external_ck
            CHECK ((external_system IS NULL) = (external_reference IS NULL));
    END IF;
END $$;
