-- 0046 · Lot pairing and level readings (#81, D-078): a made lot can name the lots it was made from (link basis made_from), and a
-- location's level readings mark it empty at or below empty_level and are compared with declared stock within level_tolerance.
-- Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.lot_genealogy DROP CONSTRAINT IF EXISTS lot_genealogy_link_basis_check;
ALTER TABLE core.lot_genealogy ADD CONSTRAINT lot_genealogy_link_basis_check
    CHECK (link_basis IN ('time_window','declared','made_from','split','merge','rework'));

ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS empty_level numeric(18,6);
ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS level_tolerance numeric(18,6);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'equipment_level_tolerance_ck') THEN
        ALTER TABLE core.equipment ADD CONSTRAINT equipment_level_tolerance_ck CHECK (level_tolerance IS NULL OR level_tolerance >= 0);
    END IF;
END $$;
