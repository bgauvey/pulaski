-- 0062 · Machine counts in production reporting (#27, D-097): where a line's output is counted, base units per count for each
-- line + material, and how far a report covers the run's machine counts. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS production_counter_equipment_id uuid REFERENCES core.equipment(id);
ALTER TABLE core.equipment_capability ADD COLUMN IF NOT EXISTS units_per_count numeric(18,6);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'capability_units_per_count_ck') THEN
        ALTER TABLE core.equipment_capability ADD CONSTRAINT capability_units_per_count_ck CHECK (units_per_count > 0);
    END IF;
END $$;

ALTER TABLE ts.production_count ADD COLUMN IF NOT EXISTS machine_through timestamptz;
