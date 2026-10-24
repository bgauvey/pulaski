-- 0028 · How commingled balances flow (D-060): a flow model and a minimum residence time per location, for genealogy through
-- silos and tanks; and a 'level' tag role, bound now and read once process values are recorded. Re-runnable; no BEGIN/COMMIT
-- (db/migrate.sh runs it in one transaction).

ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS flow_model text NOT NULL DEFAULT 'mixed';
ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS min_residence interval;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'equipment_flow_model_check' AND conrelid = 'core.equipment'::regclass) THEN
        ALTER TABLE core.equipment ADD CONSTRAINT equipment_flow_model_check CHECK (flow_model IN ('mixed','fifo'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'equipment_min_residence_check' AND conrelid = 'core.equipment'::regclass) THEN
        ALTER TABLE core.equipment ADD CONSTRAINT equipment_min_residence_check CHECK (min_residence IS NULL OR min_residence >= interval '0');
    END IF;
END $$;

ALTER TABLE core.equipment_tag DROP CONSTRAINT IF EXISTS equipment_tag_tag_role_check;
ALTER TABLE core.equipment_tag ADD CONSTRAINT equipment_tag_tag_role_check CHECK (tag_role IN (
    'state','unit_mode','stop_reason','alarm','warning',
    'processed_count','defective_count','consumed_count',
    'machine_speed','process_value','level'));
