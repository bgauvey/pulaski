-- 0037 · Stop adjustments (D-069): stops entered by hand, and stops split or merged. An adjusted stop is never changed: it is marked
-- superseded and new stops replace it, each saying where it came from. Permission downtime.adjust. Re-runnable; no BEGIN/COMMIT.

SET LOCAL client_min_messages = warning;

ALTER TABLE ts.downtime
    ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'machine',
    ADD COLUMN IF NOT EXISTS replaces_ids uuid[],
    ADD COLUMN IF NOT EXISTS adjusted_by text,
    ADD COLUMN IF NOT EXISTS adjusted_by_user_id uuid,
    ADD COLUMN IF NOT EXISTS adjusted_at timestamptz,
    ADD COLUMN IF NOT EXISTS adjustment_comment text,
    ADD COLUMN IF NOT EXISTS superseded_at timestamptz,
    ADD COLUMN IF NOT EXISTS superseded_by text;

ALTER TABLE ts.downtime
    DROP CONSTRAINT IF EXISTS downtime_source_ck,
    ADD CONSTRAINT downtime_source_ck CHECK (source IN ('machine', 'manual', 'split', 'merge')),
    DROP CONSTRAINT IF EXISTS downtime_adjusted_ck,
    ADD CONSTRAINT downtime_adjusted_ck CHECK ((source = 'machine') = (adjusted_at IS NULL)),
    DROP CONSTRAINT IF EXISTS downtime_replaces_ck,
    ADD CONSTRAINT downtime_replaces_ck CHECK ((source IN ('split', 'merge')) = (replaces_ids IS NOT NULL)),
    DROP CONSTRAINT IF EXISTS downtime_superseded_ck,
    ADD CONSTRAINT downtime_superseded_ck CHECK (superseded_at IS NULL OR ended_at IS NOT NULL);

-- The uncoded queue leaves out replaced stops.
DROP INDEX IF EXISTS ts.downtime_equipment_id_idx;
CREATE INDEX IF NOT EXISTS downtime_uncoded_idx ON ts.downtime (equipment_id)
    WHERE effective_reason_code_id IS NULL AND NOT is_micro_stop AND superseded_at IS NULL;

INSERT INTO sec.permission (code, description) VALUES
    ('downtime.adjust', 'Enter machine stops by hand, and split or merge stops')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'downtime.adjust' FROM sec.role r WHERE r.code = 'supervisor' AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

UPDATE sec.role SET description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes, enters, splits and merges machine stops. Doesn''t correct inventory.'
WHERE code = 'supervisor' AND description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes machine stops. Doesn''t correct inventory.';
