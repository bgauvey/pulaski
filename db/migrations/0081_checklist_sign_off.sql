-- A checklist's second sign-off (#126d, D-116): a checklist may need one by someone else holding checklists.sign_off before it counts.

ALTER TABLE core.checklist_response ADD COLUMN IF NOT EXISTS sign_off text;
ALTER TABLE core.checklist_response ADD COLUMN IF NOT EXISTS sign_off_by text;
ALTER TABLE core.checklist_response ADD COLUMN IF NOT EXISTS sign_off_at timestamptz;
ALTER TABLE core.checklist_response ADD COLUMN IF NOT EXISTS sign_off_note text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'checklist_response_sign_off_ck') THEN
        ALTER TABLE core.checklist_response ADD CONSTRAINT checklist_response_sign_off_ck CHECK (
            (sign_off IS NULL OR sign_off IN ('pending', 'signed', 'rejected'))
            AND (sign_off IS NULL OR result = 'passed')
            AND ((sign_off IN ('signed', 'rejected')) = (sign_off_at IS NOT NULL AND sign_off_by IS NOT NULL)));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS checklist_response_pending_ix ON core.checklist_response (site_id) WHERE sign_off = 'pending';

INSERT INTO sec.permission (code, description) VALUES
    ('checklists.sign_off', 'Give setup and first-article checklists their second sign-off, or reject them')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES ('supervisor', 'checklists.sign_off'), ('quality', 'checklists.sign_off')) AS p(role, code) ON p.role = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
