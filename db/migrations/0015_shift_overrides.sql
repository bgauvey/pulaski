-- 0015 · Shift overrides (D-042): a schedule's changes to single dates (an extra Saturday, a shift off or at other times),
-- kept with the schedule as one jsonb array like its shifts. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one
-- transaction).

ALTER TABLE core.shift_schedule
    ADD COLUMN IF NOT EXISTS overrides jsonb NOT NULL DEFAULT '[]';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'shift_schedule_overrides_check') THEN
        ALTER TABLE core.shift_schedule
            ADD CONSTRAINT shift_schedule_overrides_check CHECK (jsonb_typeof(overrides) = 'array');
    END IF;
END $$;
