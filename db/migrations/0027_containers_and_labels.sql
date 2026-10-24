-- 0027 · Containers, labels and scanning (D-059): containers holding lots, label numbers for lots and containers, and the
-- packed / unpacked movements. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction). db/app-role.sql grants
-- the new table and sequence.

CREATE SEQUENCE IF NOT EXISTS core.label_seq;


-- A pallet, tote, box or bin holding lots (D-059). Its lots keep their own number, history and genealogy; moving it moves them.
-- One site, any mix of materials, no containers inside containers. Retired rather than deleted, and only when empty.
CREATE TABLE IF NOT EXISTS core.container (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    code                    text NOT NULL,                          -- upper case, unique in the site
    kind                    text NOT NULL CHECK (kind IN ('pallet','tote','box','bin','other')),
    location_equipment_id   uuid NOT NULL REFERENCES core.equipment(id),
    label_no                bigint NOT NULL DEFAULT nextval('core.label_seq') UNIQUE,
    is_active               boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL DEFAULT now(),
    created_by              text NOT NULL,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT container_uq UNIQUE (site_id, code)
);


ALTER TABLE core.lot ADD COLUMN IF NOT EXISTS container_id uuid REFERENCES core.container(id);
ALTER TABLE core.lot ADD COLUMN IF NOT EXISTS label_no bigint NOT NULL DEFAULT nextval('core.label_seq');
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lot_label_no_key' AND conrelid = 'core.lot'::regclass) THEN
        ALTER TABLE core.lot ADD CONSTRAINT lot_label_no_key UNIQUE (label_no);
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS lot_container_ix ON core.lot (container_id) WHERE container_id IS NOT NULL;

ALTER TABLE ts.lot_event ADD COLUMN IF NOT EXISTS container_id uuid REFERENCES core.container(id);
ALTER TABLE ts.lot_event DROP CONSTRAINT IF EXISTS lot_event_event_kind_check;
ALTER TABLE ts.lot_event ADD CONSTRAINT lot_event_event_kind_check CHECK (event_kind IN (
    'created','received','moved','loaded','consumed','produced',
    'split','merged','status_changed','adjusted','scrapped','shipped','corrected','reversed','packed','unpacked'));
