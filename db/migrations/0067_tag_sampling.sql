-- 0067 · Process-value SPC (D-099): characteristics sampled automatically from process-value tags on lines, each binding with its interval.
-- The unused equipment_tag_id and sample_interval of core.quality_characteristic give way to core.characteristic_tag. Re-runnable;
-- no BEGIN/COMMIT.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'characteristic_tag') THEN
        CREATE TABLE IF NOT EXISTS core.characteristic_tag (
            id                  uuid PRIMARY KEY DEFAULT uuidv7(),
            characteristic_id   uuid NOT NULL REFERENCES core.quality_characteristic(id),
            equipment_tag_id    uuid REFERENCES core.equipment_tag(id),  -- a process_value tag; NULL once the tag is deleted (then switched off)
            line_id             uuid NOT NULL REFERENCES core.equipment(id),  -- the tag's equipment or the nearest above it that runs jobs
            site_id             uuid NOT NULL REFERENCES core.equipment(id),
            interval_minutes    smallint NOT NULL CHECK (interval_minutes BETWEEN 1 AND 1440),
            is_active           boolean NOT NULL DEFAULT true,
            sampled_through     timestamptz NOT NULL,                   -- the last interval end dealt with (a result made, or skipped)
            created_at          timestamptz NOT NULL DEFAULT now(),
            updated_at          timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT characteristic_tag_tag_ck CHECK (NOT is_active OR equipment_tag_id IS NOT NULL)
        );
        -- One active binding per characteristic and line, so a line has one automatic chart a characteristic.
        CREATE UNIQUE INDEX characteristic_tag_line_uq ON core.characteristic_tag (characteristic_id, line_id) WHERE is_active;
        CREATE INDEX characteristic_tag_tag_ix ON core.characteristic_tag (equipment_tag_id);
    END IF;
END $$;

ALTER TABLE core.quality_characteristic DROP COLUMN IF EXISTS equipment_tag_id;
ALTER TABLE core.quality_characteristic DROP COLUMN IF EXISTS sample_interval;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.characteristic_tag TO pulaski_app;
    END IF;
END $$;
