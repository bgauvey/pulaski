-- 0006 · Location and expiry rules (D-032): one material at a time, and a shelf life per material.
-- Re-runnable. Runs in one transaction under db/migrate.sh; no BEGIN/COMMIT here.

ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS single_material boolean NOT NULL DEFAULT false;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'equipment_single_material_ck') THEN
        ALTER TABLE core.equipment ADD CONSTRAINT equipment_single_material_ck CHECK (NOT single_material OR holds_inventory);
    END IF;
END $$;

ALTER TABLE core.material ADD COLUMN IF NOT EXISTS shelf_life_days integer CHECK (shelf_life_days > 0);
