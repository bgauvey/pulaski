-- 0047 · Feed point automation (#33, D-079): automated consumptions say where they came from (auto_source: backflush, time_window,
-- counter) and what caused them (source_ref: the production report or the machine count), and what they couldn't cover is kept as a
-- shortfall to resolve or dismiss. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE ts.lot_event ADD COLUMN IF NOT EXISTS auto_source text;
ALTER TABLE ts.lot_event ADD COLUMN IF NOT EXISTS source_ref uuid;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lot_event_auto_source_ck') THEN
        ALTER TABLE ts.lot_event ADD CONSTRAINT lot_event_auto_source_ck CHECK (auto_source IN ('backflush','time_window','counter'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS lot_event_source_ref_ix ON ts.lot_event (source_ref) WHERE source_ref IS NOT NULL;
CREATE INDEX IF NOT EXISTS lot_event_to_location_ix ON ts.lot_event (to_location_id, effective_at) WHERE to_location_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS lot_event_from_location_ix ON ts.lot_event (from_location_id, effective_at) WHERE from_location_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS core.feed_shortfall (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    equipment_id        uuid NOT NULL REFERENCES core.equipment(id),
    job_id              uuid REFERENCES core.job(id),
    run_id              uuid REFERENCES core.job_run(id),
    material_id         uuid REFERENCES core.material(id),
    lot_id              uuid REFERENCES core.lot(id),
    quantity            core.qty NOT NULL CHECK (quantity > 0),
    uom_id              uuid REFERENCES core.uom(id),
    source              text NOT NULL CHECK (source IN ('backflush','time_window','counter')),
    source_ref          uuid NOT NULL,
    occurred_at         timestamptz NOT NULL,
    reason              text NOT NULL CHECK (length(reason) <= 500),
    status              text NOT NULL DEFAULT 'open' CHECK (status IN ('open','resolved','dismissed')),
    resolved_lot_id     uuid REFERENCES core.lot(id),
    resolved_by         text,
    resolved_at         timestamptz,
    resolution_note     text CHECK (length(resolution_note) <= 1000),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT feed_shortfall_resolved_ck CHECK ((status = 'open') = (resolved_at IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS feed_shortfall_source_uq ON core.feed_shortfall (source_ref, equipment_id, material_id) NULLS NOT DISTINCT;
CREATE INDEX IF NOT EXISTS feed_shortfall_open_ix ON core.feed_shortfall (site_id) WHERE status = 'open';

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.feed_shortfall TO pulaski_app;
    END IF;
END $$;
