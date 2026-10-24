-- 0032 · Lot allocations (D-064): stock set aside for jobs, and the permission to allocate. Re-runnable; no BEGIN/COMMIT
-- (db/migrate.sh runs it in one transaction). db/app-role.sql's grant on every core table covers the new one.

SET LOCAL client_min_messages = warning;

-- Stock set aside for a job (D-064): a quantity of one lot, in its base unit, for one of the job's components. Active ones are left
-- out of what other jobs see as free; consuming the lot into the job uses its allocation up. Released or used up, never deleted.
CREATE TABLE IF NOT EXISTS core.lot_allocation (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    job_id              uuid NOT NULL REFERENCES core.job(id),
    material_id         uuid NOT NULL REFERENCES core.material(id),
    lot_id              uuid NOT NULL REFERENCES core.lot(id),
    quantity            core.qty NOT NULL CHECK (quantity >= 0),
    allocated_qty       core.qty NOT NULL CHECK (allocated_qty > 0),
    status              text NOT NULL CHECK (status IN ('active','consumed','released')),
    basis               text NOT NULL CHECK (basis IN ('chosen','recommended')),
    note                text CHECK (length(note) <= 500),
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by          text,
    ended_at            timestamptz,
    ended_by            text,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT lot_allocation_ended_ck CHECK ((status = 'active') = (ended_at IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS lot_allocation_active_uq ON core.lot_allocation (job_id, lot_id) WHERE status = 'active';
CREATE INDEX IF NOT EXISTS lot_allocation_lot_ix ON core.lot_allocation (lot_id) WHERE status = 'active';
CREATE INDEX IF NOT EXISTS lot_allocation_site_ix ON core.lot_allocation (site_id, material_id) WHERE status = 'active';

-- The new table is the app role's like every core table (db/app-role.sql grants them all); repeated here for an existing database.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.lot_allocation TO pulaski_app;
    END IF;
END $$;

INSERT INTO sec.permission (code, description) VALUES
    ('lot.allocate', 'Allocate lots to jobs, release allocations and refresh them')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'lot.allocate' FROM sec.role r WHERE r.code IN ('planner', 'supervisor', 'quality') AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
