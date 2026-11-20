-- 0069 · Lots filled by reports (D-104): a line's capability for a material may cut its production lots at a size, an age or a change of
-- shift, and a report then adds to the lot being filled. An operator closes that lot early by setting core.lot.produced_to, which was there
-- for this and unused. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.equipment_capability ADD COLUMN IF NOT EXISTS lot_size core.qty;
ALTER TABLE core.equipment_capability ADD COLUMN IF NOT EXISTS lot_hours numeric(6,2);
ALTER TABLE core.equipment_capability ADD COLUMN IF NOT EXISTS lot_by_shift boolean NOT NULL DEFAULT false;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'capability_lot_size_ck') THEN
        ALTER TABLE core.equipment_capability ADD CONSTRAINT capability_lot_size_ck CHECK (lot_size > 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'capability_lot_hours_ck') THEN
        ALTER TABLE core.equipment_capability ADD CONSTRAINT capability_lot_hours_ck CHECK (lot_hours > 0 AND lot_hours <= 720);
    END IF;
END $$;
