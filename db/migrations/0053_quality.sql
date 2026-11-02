-- 0053 · Quality (#65, D-085): enterprise-wide characteristics, versioned material specifications released into fixed items, and
-- quality results in ts.quality_sample checked against them. The drafted spec_limit table (limits by effective dates) is replaced, and the
-- unused draft of ts.quality_sample is rebuilt. Lot attributes may stand for a characteristic. Permissions quality.view, quality.record,
-- quality.decide and specs.manage. Re-runnable; no BEGIN/COMMIT.

-- Characteristics: enterprise-wide for specifications; where it is measured is optional.
ALTER TABLE core.quality_characteristic ALTER COLUMN site_id DROP NOT NULL;
ALTER TABLE core.quality_characteristic ALTER COLUMN equipment_id DROP NOT NULL;
ALTER TABLE core.quality_characteristic ADD COLUMN IF NOT EXISTS description text;
ALTER TABLE core.quality_characteristic ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE core.quality_characteristic ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE core.quality_characteristic DROP CONSTRAINT IF EXISTS characteristic_uq;
CREATE UNIQUE INDEX IF NOT EXISTS characteristic_code_uq
    ON core.quality_characteristic (coalesce(site_id, '00000000-0000-0000-0000-000000000000'::uuid), code);

ALTER TABLE core.lot_attribute ADD COLUMN IF NOT EXISTS characteristic_id uuid REFERENCES core.quality_characteristic(id);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lot_attribute_characteristic_ck') THEN
        ALTER TABLE core.lot_attribute ADD CONSTRAINT lot_attribute_characteristic_ck CHECK (characteristic_id IS NULL OR data_type = 'number');
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS core.material_spec (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    material_id         uuid NOT NULL REFERENCES core.material(id),
    is_active           boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT material_spec_uq UNIQUE (material_id)
);

CREATE TABLE IF NOT EXISTS core.material_spec_version (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    spec_id             uuid NOT NULL REFERENCES core.material_spec(id),
    version_number      integer NOT NULL CHECK (version_number > 0),
    status              text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','released','obsolete')),
    definition          jsonb NOT NULL DEFAULT '{"items":[]}',
    released_at         timestamptz,
    released_by         text,
    note                text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT material_spec_version_uq UNIQUE (spec_id, version_number),
    CONSTRAINT material_spec_version_released_ck CHECK ((status = 'draft') = (released_at IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS material_spec_version_draft_uq ON core.material_spec_version (spec_id) WHERE status = 'draft';
CREATE UNIQUE INDEX IF NOT EXISTS material_spec_version_released_uq ON core.material_spec_version (spec_id) WHERE status = 'released';

CREATE TABLE IF NOT EXISTS core.material_spec_item (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    spec_version_id     uuid NOT NULL REFERENCES core.material_spec_version(id),
    characteristic_id   uuid NOT NULL REFERENCES core.quality_characteristic(id),
    lsl                 numeric,
    target              numeric,
    usl                 numeric,
    max_defects         integer CHECK (max_defects >= 0),
    hold_on_fail        boolean NOT NULL DEFAULT false,
    sort_order          integer NOT NULL DEFAULT 0,
    CONSTRAINT material_spec_item_uq UNIQUE (spec_version_id, characteristic_id),
    CONSTRAINT material_spec_item_order_ck CHECK (lsl IS NULL OR usl IS NULL OR lsl <= usl)
);

-- The drafted results table was never written to: rebuild it in its new shape (its partitions go with it), then drop spec_limit.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'ts' AND table_name = 'quality_sample' AND column_name = 'site_id') THEN
        DROP TABLE IF EXISTS ts.quality_sample;
        CREATE TABLE ts.quality_sample (
            id                      uuid NOT NULL DEFAULT uuidv7(),
            occurred_at             timestamptz NOT NULL,
            site_id                 uuid NOT NULL REFERENCES core.equipment(id),
            characteristic_id       uuid NOT NULL REFERENCES core.quality_characteristic(id),
            equipment_id            uuid REFERENCES core.equipment(id),
            job_id                  uuid REFERENCES core.job(id),
            run_id                  uuid,
            lot_id                  uuid REFERENCES core.lot(id),
            material_id             uuid NOT NULL REFERENCES core.material(id),
            subgroup_size           smallint NOT NULL,
            measured_values         numeric[],
            mean                    numeric,
            range                   numeric,
            std_dev                 numeric,
            inspected_count         integer,
            defect_count            integer,
            spec_item_id            uuid REFERENCES core.material_spec_item(id),
            conforms                boolean,
            control_limit_id        uuid REFERENCES core.control_limit(id),
            source                  text NOT NULL CHECK (source IN ('auto','manual','lab','lot_attribute')),
            entered_by_user_id      uuid,
            recorded_by             text NOT NULL,
            recorded_at             timestamptz NOT NULL,
            comment                 text,
            is_excluded             boolean NOT NULL DEFAULT false,
            exclusion_reason_id     uuid REFERENCES core.reason_code(id),
            excluded_note           text,
            excluded_by             text,
            excluded_at             timestamptz,
            PRIMARY KEY (id, occurred_at),
            CONSTRAINT quality_sample_kind_ck CHECK (measured_values IS NOT NULL OR (inspected_count IS NOT NULL AND defect_count IS NOT NULL)),
            CONSTRAINT quality_sample_target_ck CHECK (lot_id IS NOT NULL OR job_id IS NOT NULL),
            CONSTRAINT quality_sample_excluded_ck CHECK (is_excluded = (excluded_at IS NOT NULL))
        ) PARTITION BY RANGE (occurred_at);
        CREATE INDEX ON ts.quality_sample (characteristic_id, occurred_at);
        CREATE INDEX quality_sample_site_ix ON ts.quality_sample (site_id, occurred_at);
        CREATE INDEX quality_sample_lot_ix ON ts.quality_sample (lot_id, occurred_at) WHERE lot_id IS NOT NULL;
        CREATE INDEX quality_sample_job_ix ON ts.quality_sample (job_id, occurred_at) WHERE job_id IS NOT NULL;
        PERFORM ts.ensure_partitions(3);
    END IF;
END $$;

DROP TABLE IF EXISTS core.spec_limit;

INSERT INTO sec.permission (code, description) VALUES
    ('quality.view', 'See specifications, characteristics and quality results'),
    ('quality.record', 'Record quality results against runs and lots'),
    ('quality.decide', 'Exclude quality results and reject held lots on quality'),
    ('specs.manage', 'Maintain quality characteristics and material specifications')
ON CONFLICT (code) DO NOTHING;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES
    ('quality', 'quality.view'), ('quality', 'quality.record'), ('quality', 'quality.decide'), ('quality', 'specs.manage'),
    ('supervisor', 'quality.view'), ('supervisor', 'quality.record'),
    ('operator', 'quality.view'), ('operator', 'quality.record'),
    ('planner', 'quality.view'),
    ('manager', 'quality.view'), ('auditor', 'quality.view'),
    ('process_engineer', 'quality.view'), ('process_engineer', 'specs.manage')) AS p(role, code) ON p.role = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- Starting roles' descriptions, where an administrator hasn't changed them.
UPDATE sec.role SET description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes, enters, splits and merges machine stops; records quality results. Doesn''t correct inventory.'
WHERE code = 'supervisor' AND is_system
  AND description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes, enters, splits and merges machine stops. Doesn''t correct inventory.';
UPDATE sec.role SET description = 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops, records quality results.'
WHERE code = 'operator' AND is_system
  AND description = 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops.';
UPDATE sec.role SET description = 'Holds and releases lots, corrects inventory, decides on partial batches, keeps lot attributes and certificates, maintains specifications, records quality results, excludes them and rejects lots on quality.'
WHERE code = 'quality' AND is_system
  AND description = 'Holds and releases lots, corrects inventory, decides on partial batches, and keeps lot attributes and certificates.';
UPDATE sec.role SET description = 'Maintains processes, BOMs, capabilities, changeover times and specifications. Sees equipment, materials, units and the schedule.'
WHERE code = 'process_engineer' AND is_system
  AND description = 'Maintains processes, BOMs, capabilities and changeover times. Sees equipment, materials, units and the schedule.';

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.material_spec, core.material_spec_version, core.material_spec_item TO pulaski_app;
        GRANT SELECT, INSERT ON ts.quality_sample TO pulaski_app;
        GRANT UPDATE (is_excluded, exclusion_reason_id, excluded_note, excluded_by, excluded_at) ON ts.quality_sample TO pulaski_app;
    END IF;
END $$;
