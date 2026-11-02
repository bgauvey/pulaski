-- 0052 · Allocation refinements (#82, D-084): a site's allocation settings (keep allocations current, hard reservation, the preference
-- order after first expiry), alternates on BOM lines, and allocations of an alternate recording the component they stand in for and the ratio.
-- Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.allocation_settings (
    site_id             uuid PRIMARY KEY REFERENCES core.equipment(id),
    keep_current        boolean NOT NULL DEFAULT false,
    hard_reservation    boolean NOT NULL DEFAULT false,
    preferences         text[] NOT NULL DEFAULT '{}'
                        CHECK (preferences <@ ARRAY['near_line','in_container','whole_lot','part_lots_first']::text[]),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

-- When the background refresh last started for a site (#82); its own row, so a refresh never changes the settings' version.
CREATE TABLE IF NOT EXISTS core.allocation_refresh (
    site_id             uuid PRIMARY KEY REFERENCES core.equipment(id),
    refreshed_at        timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS core.bom_item_alternate (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    bom_item_id         uuid NOT NULL REFERENCES core.bom_item(id),
    material_id         uuid NOT NULL REFERENCES core.material(id),
    ratio               numeric(18,6) NOT NULL CHECK (ratio > 0),
    priority            integer NOT NULL DEFAULT 1 CHECK (priority > 0),
    CONSTRAINT bom_item_alternate_uq UNIQUE (bom_item_id, material_id)
);

ALTER TABLE core.lot_allocation ADD COLUMN IF NOT EXISTS for_material_id uuid REFERENCES core.material(id);
ALTER TABLE core.lot_allocation ADD COLUMN IF NOT EXISTS ratio numeric(18,6);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lot_allocation_alternate_ck') THEN
        ALTER TABLE core.lot_allocation ADD CONSTRAINT lot_allocation_alternate_ck
            CHECK ((for_material_id IS NULL) = (ratio IS NULL) AND (ratio IS NULL OR ratio > 0));
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.allocation_settings, core.allocation_refresh, core.bom_item_alternate TO pulaski_app;
    END IF;
END $$;
