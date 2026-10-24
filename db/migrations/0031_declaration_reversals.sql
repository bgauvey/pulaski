-- 0031 · Reversing declarations (D-063): which report made a lot and its counts, partial batches whose report was reversed,
-- and the permission to reverse reports. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

SET LOCAL client_min_messages = warning;

ALTER TABLE ts.production_count ADD COLUMN IF NOT EXISTS report_id uuid;
CREATE INDEX IF NOT EXISTS production_count_report_ix ON ts.production_count (report_id) WHERE report_id IS NOT NULL;

ALTER TABLE ts.lot_event ADD COLUMN IF NOT EXISTS report_id uuid;
CREATE INDEX IF NOT EXISTS lot_event_reverses_ix ON ts.lot_event (lot_id, reverses_event_id) WHERE reverses_event_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS lot_event_report_ix ON ts.lot_event (report_id) WHERE report_id IS NOT NULL;

-- A partial batch whose report was reversed: it no longer counts as the run's one partial batch.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'partial_batch_run_uq' AND conrelid = 'core.partial_batch'::regclass) THEN
        ALTER TABLE core.partial_batch DROP CONSTRAINT partial_batch_run_uq;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'core.partial_batch'::regclass AND contype = 'c'
                   AND pg_get_constraintdef(oid) LIKE '%reversed%' AND conname <> 'partial_batch_decided_ck') THEN
        ALTER TABLE core.partial_batch DROP CONSTRAINT IF EXISTS partial_batch_status_check;
        ALTER TABLE core.partial_batch ADD CONSTRAINT partial_batch_status_check
            CHECK (status IN ('accepted','pending','approved','rejected','reversed'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'partial_batch_decided_ck' AND conrelid = 'core.partial_batch'::regclass
                   AND pg_get_constraintdef(oid) LIKE '%reversed%') THEN
        ALTER TABLE core.partial_batch DROP CONSTRAINT IF EXISTS partial_batch_decided_ck;
        ALTER TABLE core.partial_batch ADD CONSTRAINT partial_batch_decided_ck CHECK (CASE WHEN status IN ('approved','rejected') THEN decided_at IS NOT NULL
                                                                                           WHEN status = 'reversed' THEN true ELSE decided_at IS NULL END);
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS partial_batch_run_uq ON core.partial_batch (run_id) WHERE status <> 'reversed';

INSERT INTO sec.permission (code, description) VALUES
    ('production.correct', 'Reverse production reports: their counts, the run''s totals and the lots they made')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'production.correct' FROM sec.role r WHERE r.code IN ('supervisor', 'auditor') AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- The starting roles' descriptions, unless someone has changed them.
UPDATE sec.role SET description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes machine stops. Doesn''t correct inventory.'
WHERE code = 'supervisor' AND description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; records and approves labor; codes machine stops. Doesn''t correct inventory.';
UPDATE sec.role SET description = 'Views everything, corrects inventory and reverses production reports; counts stock. Doesn''t plan or run jobs.'
WHERE code = 'auditor' AND description = 'Views everything and corrects inventory; counts stock. Doesn''t plan or run jobs.';
