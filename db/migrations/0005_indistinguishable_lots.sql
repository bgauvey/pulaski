-- 0005 · Indistinguishable lots (D-031): locations whose lots mix into one balance per material,
-- and balances with no lot number. Re-runnable. Runs in one transaction under db/migrate.sh.

ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS commingles_lots boolean NOT NULL DEFAULT false;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'equipment_commingles_ck') THEN
        ALTER TABLE core.equipment ADD CONSTRAINT equipment_commingles_ck CHECK (NOT commingles_lots OR holds_inventory);
    END IF;
END $$;

-- A balance has no lot number (it was '@' and its location's code).
ALTER TABLE core.lot ALTER COLUMN lot_number DROP NOT NULL;
UPDATE core.lot SET lot_number = NULL WHERE is_bulk AND lot_number IS NOT NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lot_number_bulk_ck') THEN
        ALTER TABLE core.lot ADD CONSTRAINT lot_number_bulk_ck CHECK ((lot_number IS NULL) = is_bulk);
    END IF;
END $$;
