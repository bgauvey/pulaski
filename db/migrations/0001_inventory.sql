-- 0001 · Inventory (D-027): bulk balances, declared production, the lot ledger and the genealogy trace.
-- Re-runnable: every change is guarded, so it is safe on a database already upgraded by hand.
-- Runs in one transaction under db/migrate.sh; no BEGIN/COMMIT here.

ALTER TABLE core.lot
    ADD COLUMN IF NOT EXISTS is_bulk boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS produced_by_job_id uuid REFERENCES core.job(id);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lot_bulk_location_ck') THEN
        ALTER TABLE core.lot ADD CONSTRAINT lot_bulk_location_ck CHECK (NOT is_bulk OR location_equipment_id IS NOT NULL);
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS lot_material_ix ON core.lot (material_id, site_id) WHERE status IN ('available','on_hold','quarantine');
CREATE INDEX IF NOT EXISTS lot_job_ix ON core.lot (produced_by_job_id) WHERE produced_by_job_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS lot_bulk_uq ON core.lot (material_id, location_equipment_id) WHERE is_bulk;

ALTER TABLE core.lot_genealogy ADD COLUMN IF NOT EXISTS job_id uuid REFERENCES core.job(id);
CREATE UNIQUE INDEX IF NOT EXISTS lot_genealogy_edge_uq ON core.lot_genealogy (parent_lot_id, child_lot_id, link_basis) WHERE superseded_at IS NULL;

ALTER TABLE ts.lot_event
    ADD COLUMN IF NOT EXISTS qty_after core.qty,
    ADD COLUMN IF NOT EXISTS job_id uuid REFERENCES core.job(id),
    ADD COLUMN IF NOT EXISTS comment text CHECK (length(comment) <= 1000),
    ADD COLUMN IF NOT EXISTS actor_id text;
CREATE INDEX IF NOT EXISTS lot_event_job_id_idx ON ts.lot_event (job_id) WHERE job_id IS NOT NULL;

CREATE OR REPLACE FUNCTION core.trace_forward(p_lot_id uuid)
RETURNS TABLE (lot_id uuid, via_lot_id uuid, depth integer)
LANGUAGE sql STABLE AS $$
    WITH RECURSIVE walk (lot_id, via_lot_id, depth) AS (
        SELECT p_lot_id, NULL::uuid, 0
        UNION ALL
        SELECT g.child_lot_id, w.lot_id, w.depth + 1
        FROM walk w
        JOIN core.lot_genealogy g ON g.parent_lot_id = w.lot_id AND g.superseded_at IS NULL
        WHERE w.depth < 50
    ) CYCLE lot_id SET is_cycle USING path
    SELECT walk.lot_id, walk.via_lot_id, walk.depth FROM walk WHERE NOT walk.is_cycle
$$;

CREATE OR REPLACE FUNCTION core.trace_backward(p_lot_id uuid)
RETURNS TABLE (lot_id uuid, via_lot_id uuid, depth integer)
LANGUAGE sql STABLE AS $$
    WITH RECURSIVE walk (lot_id, via_lot_id, depth) AS (
        SELECT p_lot_id, NULL::uuid, 0
        UNION ALL
        SELECT g.parent_lot_id, w.lot_id, w.depth + 1
        FROM walk w
        JOIN core.lot_genealogy g ON g.child_lot_id = w.lot_id AND g.superseded_at IS NULL
        WHERE w.depth < 50
    ) CYCLE lot_id SET is_cycle USING path
    SELECT walk.lot_id, walk.via_lot_id, walk.depth FROM walk WHERE NOT walk.is_cycle
$$;
