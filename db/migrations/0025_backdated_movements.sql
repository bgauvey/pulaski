-- 0025 · Back-dated inventory movements (D-057): each lot movement keeps when it was recorded (occurred_at) and gains when it
-- counts (effective_at); genealogy links likewise. Existing rows take effect when recorded. Re-runnable; no BEGIN/COMMIT
-- (db/migrate.sh runs it in one transaction).

ALTER TABLE ts.lot_event ADD COLUMN IF NOT EXISTS effective_at timestamptz;
UPDATE ts.lot_event SET effective_at = occurred_at WHERE effective_at IS NULL;
ALTER TABLE ts.lot_event ALTER COLUMN effective_at SET NOT NULL;
CREATE INDEX IF NOT EXISTS lot_event_lot_effective_idx ON ts.lot_event (lot_id, effective_at);

ALTER TABLE core.lot_genealogy ADD COLUMN IF NOT EXISTS effective_at timestamptz;
UPDATE core.lot_genealogy SET effective_at = derived_at WHERE effective_at IS NULL;
ALTER TABLE core.lot_genealogy ALTER COLUMN effective_at SET DEFAULT now();
ALTER TABLE core.lot_genealogy ALTER COLUMN effective_at SET NOT NULL;

INSERT INTO sec.permission (code, description) VALUES
    ('inventory.backdate', 'Back-date receipts, consumption, production, write-ups and write-offs by up to 31 days')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'inventory.backdate' FROM sec.role r WHERE r.code IN ('quality', 'auditor') AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
