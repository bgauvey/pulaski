-- 0064 · Control limits (D-099): the drafted core.control_limit, never written to, is rebuilt per characteristic and material, optionally
-- one line, with its chart, how it was set and who approved it. Characteristics recorded with the old fixed np chart move to p. Re-runnable;
-- no BEGIN/COMMIT.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'control_limit' AND column_name = 'chart_type') THEN
        DROP TABLE IF EXISTS core.control_limit CASCADE;   -- takes ts.quality_sample's foreign key with it; re-added below
        CREATE TABLE core.control_limit (
            id                  uuid PRIMARY KEY DEFAULT uuidv7(),
            characteristic_id   uuid NOT NULL REFERENCES core.quality_characteristic(id),
            material_id         uuid NOT NULL REFERENCES core.material(id),
            equipment_id        uuid REFERENCES core.equipment(id),     -- one line's own limits, used before the material's; NULL: every line
            chart_type          text NOT NULL CHECK (chart_type IN ('xbar_r','xbar_s','i_mr','p','np','c','u')),
            subgroup_size       smallint NOT NULL CHECK (subgroup_size >= 1),
            center_line         double precision NOT NULL,
            ucl                 double precision NOT NULL,
            lcl                 double precision NOT NULL,
            dispersion_center   double precision,                       -- R-bar, S-bar or MR-bar
            dispersion_ucl      double precision,
            dispersion_lcl      double precision,
            method              text NOT NULL CHECK (method IN ('computed','manual')),
            baseline_from       timestamptz,
            baseline_to         timestamptz,
            baseline_subgroups  integer,
            left_out_sample_ids uuid[] NOT NULL DEFAULT '{}',           -- results the reviewer left out of the baseline
            note                text CHECK (length(note) <= 1000),      -- required for manual limits
            effective_from      timestamptz NOT NULL,
            effective_to        timestamptz,
            approved_by         text NOT NULL,
            approved_at         timestamptz NOT NULL,
            ended_by            text,
            created_at          timestamptz NOT NULL DEFAULT now(),
            updated_at          timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT control_limit_order_ck CHECK (lcl <= center_line AND center_line <= ucl),
            CONSTRAINT control_limit_period_ck CHECK (effective_to IS NULL OR effective_to > effective_from),
            CONSTRAINT control_limit_baseline_ck CHECK ((method = 'computed') = (baseline_from IS NOT NULL AND baseline_to IS NOT NULL)),
            CONSTRAINT control_limit_manual_ck CHECK (method <> 'manual' OR note IS NOT NULL),
            CONSTRAINT control_limit_no_overlap EXCLUDE USING gist (
                characteristic_id WITH =,
                material_id WITH =,
                (coalesce(equipment_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
                tstzrange(effective_from, effective_to) WITH &&)
        );
        CREATE INDEX control_limit_scope_ix ON core.control_limit (characteristic_id, material_id, effective_from);
        ALTER TABLE ts.quality_sample ADD CONSTRAINT quality_sample_control_limit_id_fkey FOREIGN KEY (control_limit_id) REFERENCES core.control_limit(id);
        UPDATE core.quality_characteristic SET chart_type = 'p' WHERE chart_type = 'np' AND subgroup_size = 1;
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.control_limit TO pulaski_app;
    END IF;
END $$;
