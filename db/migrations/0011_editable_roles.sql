-- 0011 · Editable roles (D-037): roles move from code to data. An administrator creates roles and chooses their
-- permissions; only 'administrator' stays fixed in code. A permission taken away keeps its row, flagged, since
-- the app role has no DELETE. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

ALTER TABLE sec.role
    ADD COLUMN IF NOT EXISTS description text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'role_code_ck') THEN
        ALTER TABLE sec.role ADD CONSTRAINT role_code_ck CHECK (code ~ '^[a-z][a-z0-9_]{1,49}$');
    END IF;
END $$;

ALTER TABLE sec.role_permission
    ADD COLUMN IF NOT EXISTS is_granted boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS changed_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS changed_by_user_id uuid REFERENCES sec.app_user(id);

-- The starting roles' descriptions, where none has been written yet.
UPDATE sec.role r SET description = v.description FROM (VALUES
    ('administrator', 'Everything, everywhere, including users and roles.'),
    ('planner', 'Plans and sequences jobs, manages work orders, planning rates and materials. Views the floor and inventory.'),
    ('supervisor', 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches. Doesn''t correct inventory.'),
    ('operator', 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots.'),
    ('quality', 'Holds and releases lots, corrects inventory and decides on partial batches.'),
    ('manager', 'Views everything; reopens jobs, closes work orders, maintains reason codes and the site''s settings.'),
    ('auditor', 'Views everything and corrects inventory; counts stock. Doesn''t plan or run jobs.'),
    ('process_engineer', 'Maintains processes, BOMs and capabilities.')
) AS v(code, description)
WHERE r.code = v.code AND r.description = '';

-- The administrator's permissions come from the code, not rows.
UPDATE sec.role_permission rp SET is_granted = false
FROM sec.role r WHERE r.id = rp.role_id AND r.code = 'administrator' AND rp.is_granted;

CREATE OR REPLACE VIEW sec.v_user_permission AS
SELECT ra.app_user_id,
       rp.permission_code,
       c.descendant_id AS equipment_id
FROM sec.role_assignment ra
JOIN sec.role r ON r.id = ra.role_id AND r.is_active
JOIN sec.role_permission rp ON rp.role_id = ra.role_id AND rp.is_granted
JOIN core.equipment_closure c ON c.ancestor_id = ra.scope_equipment_id
WHERE ra.app_user_id IS NOT NULL
  AND ra.valid_from <= now()
  AND (ra.valid_to IS NULL OR ra.valid_to > now());

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON sec.role, sec.permission, sec.role_permission TO pulaski_app;
    END IF;
END $$;
