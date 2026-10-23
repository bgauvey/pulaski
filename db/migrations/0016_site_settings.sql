-- 0016 · Site settings (D-043): calendar exceptions are maintained on screen, so they are switched off rather than
-- deleted and keep when they were made and changed; a view permission opens the Site settings screen, and the starting
-- roles that plan, run or oversee a site get it. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

ALTER TABLE core.calendar_exception
    ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

INSERT INTO sec.permission (code, description) VALUES
    ('site_settings.view', 'See a site''s settings: changeover mode, reporting tolerances and calendar exceptions'),
    ('site_settings.manage', 'Set a site''s changeover mode, reporting tolerances and calendar exceptions')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

-- The starting roles gain it; other roles gain nothing (an administrator decides).
INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, v.permission_code FROM (VALUES
    ('planner', 'site_settings.view'),
    ('supervisor', 'site_settings.view'),
    ('manager', 'site_settings.view'),
    ('auditor', 'site_settings.view')
) AS v(role_code, permission_code)
JOIN sec.role r ON r.code = v.role_code AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
