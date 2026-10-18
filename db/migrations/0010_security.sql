-- 0010 · Role-based security (D-037): local sign-in with hashed passwords, global administrator
-- assignments, and the code-defined roles and permissions. Re-runnable. Runs in one transaction
-- under db/migrate.sh; no BEGIN/COMMIT here.

ALTER TABLE sec.app_user
    ADD COLUMN IF NOT EXISTS username text,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
CREATE UNIQUE INDEX IF NOT EXISTS app_user_username_uq ON sec.app_user (lower(username)) WHERE username IS NOT NULL;

-- A role held everywhere (the administrator) has no scope.
ALTER TABLE sec.role_assignment ALTER COLUMN scope_equipment_id DROP NOT NULL;
ALTER TABLE sec.role_assignment ADD COLUMN IF NOT EXISTS ended_by_user_id uuid REFERENCES sec.app_user(id);

CREATE TABLE IF NOT EXISTS sec.local_credential (
    app_user_id         uuid PRIMARY KEY REFERENCES sec.app_user(id),
    password_hash       text NOT NULL,
    must_change         boolean NOT NULL DEFAULT true,
    failed_attempts     integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until        timestamptz,
    changed_at          timestamptz NOT NULL DEFAULT now()
);

INSERT INTO sec.permission (code, description) VALUES
    ('schedule.plan', 'Plan jobs: create, sequence, pin, assign to lines and cancel them'),
    ('work_order.manage', 'Create work orders, dispatch, un-dispatch and firm them'),
    ('work_order.close', 'Cancel and close work orders'),
    ('rate.modify', 'Set and clear the modified planning rate'),
    ('job.execute', 'Start, pause, resume and complete jobs'),
    ('job.reopen', 'Reopen a completed job'),
    ('production.report', 'Report production and consumption into a job''s runs'),
    ('lot.receive', 'Receive lots'),
    ('lot.move', 'Move lots between locations'),
    ('lot.ship', 'Ship lots'),
    ('lot.hold', 'Put lots on hold or in quarantine and release them'),
    ('inventory.correct', 'Correct inventory: write up, write off, correct, reverse, split and merge lots'),
    ('stock_count.manage', 'Start, enter, post and cancel stock counts'),
    ('partial_batch.decide', 'Approve or reject partial batches (not one''s own)'),
    ('materials.manage', 'Maintain materials, classes, conversions, batching and shelf life; import materials'),
    ('processes.manage', 'Maintain processes, BOMs and capabilities'),
    ('units.manage', 'Maintain units of measure'),
    ('reasons.manage', 'Maintain reason codes'),
    ('equipment.manage', 'Maintain equipment: sites, lines, locations'),
    ('site_settings.manage', 'Set a site''s changeover mode and reporting tolerances'),
    ('security.manage', 'Maintain users and their roles')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role (code, name, is_system) VALUES
    ('administrator', 'Administrator', true),
    ('planner', 'Planner', true),
    ('supervisor', 'Supervisor', true),
    ('operator', 'Operator', true),
    ('quality', 'Quality', true),
    ('manager', 'Manager', true),
    ('auditor', 'Auditor', true),
    ('process_engineer', 'Process engineer', true)
ON CONFLICT (code) DO NOTHING;

-- The starting roles' permissions, where missing; 0011 makes roles editable, so nothing here overwrites them.
INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, v.permission_code FROM (VALUES
    ('administrator', 'schedule.plan'),
    ('administrator', 'work_order.manage'),
    ('administrator', 'work_order.close'),
    ('administrator', 'rate.modify'),
    ('administrator', 'job.execute'),
    ('administrator', 'job.reopen'),
    ('administrator', 'production.report'),
    ('administrator', 'lot.receive'),
    ('administrator', 'lot.move'),
    ('administrator', 'lot.ship'),
    ('administrator', 'lot.hold'),
    ('administrator', 'inventory.correct'),
    ('administrator', 'stock_count.manage'),
    ('administrator', 'partial_batch.decide'),
    ('administrator', 'materials.manage'),
    ('administrator', 'processes.manage'),
    ('administrator', 'units.manage'),
    ('administrator', 'reasons.manage'),
    ('administrator', 'equipment.manage'),
    ('administrator', 'site_settings.manage'),
    ('administrator', 'security.manage'),
    ('planner', 'schedule.plan'),
    ('planner', 'work_order.manage'),
    ('planner', 'work_order.close'),
    ('planner', 'rate.modify'),
    ('planner', 'materials.manage'),
    ('supervisor', 'job.execute'),
    ('supervisor', 'job.reopen'),
    ('supervisor', 'production.report'),
    ('supervisor', 'lot.receive'),
    ('supervisor', 'lot.move'),
    ('supervisor', 'lot.ship'),
    ('supervisor', 'lot.hold'),
    ('supervisor', 'stock_count.manage'),
    ('supervisor', 'partial_batch.decide'),
    ('operator', 'job.execute'),
    ('operator', 'production.report'),
    ('operator', 'lot.receive'),
    ('operator', 'lot.move'),
    ('quality', 'lot.hold'),
    ('quality', 'inventory.correct'),
    ('quality', 'partial_batch.decide'),
    ('manager', 'job.reopen'),
    ('manager', 'work_order.close'),
    ('manager', 'reasons.manage'),
    ('manager', 'site_settings.manage'),
    ('auditor', 'inventory.correct'),
    ('auditor', 'stock_count.manage'),
    ('process_engineer', 'processes.manage')
) AS v(role_code, permission_code)
JOIN sec.role r ON r.code = v.role_code
ON CONFLICT (role_id, permission_code) DO NOTHING;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT USAGE ON SCHEMA sec TO pulaski_app;
        GRANT SELECT, INSERT, UPDATE ON sec.app_user, sec.role_assignment, sec.local_credential TO pulaski_app;
        GRANT SELECT ON sec.role, sec.permission, sec.role_permission TO pulaski_app;
    END IF;
END $$;
