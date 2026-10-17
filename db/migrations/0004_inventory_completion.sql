-- 0004 · Completing inventory (D-030): cancelled lots, corrections and reversals, shipment
-- references, and stock counts. Re-runnable. Runs in one transaction under db/migrate.sh.

ALTER TABLE core.lot DROP CONSTRAINT IF EXISTS lot_status_check;
ALTER TABLE core.lot ADD CONSTRAINT lot_status_check CHECK (status IN (
    'planned','in_production','available','on_hold','quarantine',
    'consumed','scrapped','shipped','cancelled'));

ALTER TABLE ts.lot_event DROP CONSTRAINT IF EXISTS lot_event_event_kind_check;
ALTER TABLE ts.lot_event ADD CONSTRAINT lot_event_event_kind_check CHECK (event_kind IN (
    'created','received','moved','loaded','consumed','produced',
    'split','merged','status_changed','adjusted','scrapped','shipped','corrected','reversed'));

ALTER TABLE ts.lot_event
    ADD COLUMN IF NOT EXISTS reference text CHECK (length(reference) <= 100),
    ADD COLUMN IF NOT EXISTS reverses_event_id uuid,
    ADD COLUMN IF NOT EXISTS reverses_occurred_at timestamptz;

CREATE TABLE IF NOT EXISTS core.stock_count (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    count_number            text NOT NULL,
    location_equipment_id   uuid NOT NULL REFERENCES core.equipment(id),
    status                  text NOT NULL DEFAULT 'open' CHECK (status IN ('open','posted','cancelled')),
    note                    text CHECK (length(note) <= 1000),
    created_by              text NOT NULL,
    posted_at               timestamptz,
    posted_by               text,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT stock_count_number_uq UNIQUE (site_id, count_number),
    CONSTRAINT stock_count_posted_ck CHECK ((status = 'posted') = (posted_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS stock_count_site_ix ON core.stock_count (site_id, created_at);

CREATE TABLE IF NOT EXISTS core.stock_count_line (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    stock_count_id      uuid NOT NULL REFERENCES core.stock_count(id),
    lot_id              uuid NOT NULL REFERENCES core.lot(id),
    expected_qty        core.qty NOT NULL,
    counted_qty         core.qty CHECK (counted_qty >= 0),
    counted_at          timestamptz,
    counted_by          text,
    CONSTRAINT stock_count_line_uq UNIQUE (stock_count_id, lot_id),
    CONSTRAINT stock_count_line_counted_ck CHECK ((counted_qty IS NULL) = (counted_at IS NULL))
);
