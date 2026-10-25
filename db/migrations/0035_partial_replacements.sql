-- 0035 · Replacement jobs for a partial batch (D-067): a job planned with a partial last batch, and the permission to plan one.
-- Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

SET LOCAL client_min_messages = warning;

ALTER TABLE core.job ADD COLUMN IF NOT EXISTS partial_batch_planned boolean NOT NULL DEFAULT false;

INSERT INTO sec.permission (code, description) VALUES
    ('schedule.plan_partial', 'Replace a cancelled job with exactly what is left, even when that isn''t whole batches')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'schedule.plan_partial' FROM sec.role r WHERE r.code = 'planner' AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
