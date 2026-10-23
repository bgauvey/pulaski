-- 0021 · Stops (D-050): machine stops in ts.downtime are opened and closed by machine data ingest and coded by operators, so
-- the app may update them; each keeps the unit mode it stopped in and who coded it. A machine's live state points at its open
-- stop. Permissions machines.view and downtime.code. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

ALTER TABLE ts.downtime
    ADD COLUMN IF NOT EXISTS unit_mode smallint NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS coded_by text;

ALTER TABLE core.equipment_live_state
    ADD COLUMN IF NOT EXISTS open_downtime_id uuid,
    ADD COLUMN IF NOT EXISTS open_downtime_started_at timestamptz,
    DROP CONSTRAINT IF EXISTS equipment_live_downtime_ck,
    ADD CONSTRAINT equipment_live_downtime_ck CHECK ((open_downtime_id IS NULL) = (open_downtime_started_at IS NULL));

INSERT INTO sec.permission (code, description) VALUES
    ('machines.view', 'See machines: their live state and their stops'),
    ('downtime.code', 'Code machine stops: confirm or change their reason')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

-- The starting roles gain theirs; other roles gain nothing (an administrator decides).
INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, v.permission_code FROM (VALUES
    ('planner', 'machines.view'),
    ('supervisor', 'machines.view'),
    ('supervisor', 'downtime.code'),
    ('operator', 'machines.view'),
    ('operator', 'downtime.code'),
    ('quality', 'machines.view'),
    ('manager', 'machines.view'),
    ('auditor', 'machines.view')
) AS v(role_code, permission_code)
JOIN sec.role r ON r.code = v.role_code AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

UPDATE sec.role SET description = v.description FROM (VALUES
    ('supervisor', 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; records and approves labor; codes machine stops. Doesn''t correct inventory.'),
    ('operator', 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops.')
) AS v(code, description)
WHERE sec.role.code = v.code AND sec.role.is_system;
