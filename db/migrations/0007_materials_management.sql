-- 0007 · Materials management (D-033): a description for materials, and classes that can be
-- switched off. Re-runnable. Runs in one transaction under db/migrate.sh; no BEGIN/COMMIT here.

ALTER TABLE core.material ADD COLUMN IF NOT EXISTS description text CHECK (length(description) <= 2000);

ALTER TABLE core.material_class
    ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
