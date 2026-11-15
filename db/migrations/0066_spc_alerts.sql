-- 0066 · SPC alerts (D-099): the drafted core.spc_alert and ts.spc_violation, never written to, are rebuilt. An alert opens when a
-- recorded result breaks one of its characteristic's run rules, one open per chart, and is closed with a cause and the action taken; each
-- breaking result is kept in ts.spc_violation. Re-runnable; no BEGIN/COMMIT.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'spc_alert' AND column_name = 'breaks') THEN
        DROP TABLE IF EXISTS ts.spc_violation;
        DROP TABLE IF EXISTS core.spc_alert;
        CREATE TABLE core.spc_alert (
            id                      uuid PRIMARY KEY DEFAULT uuidv7(),
            site_id                 uuid NOT NULL REFERENCES core.equipment(id),
            characteristic_id       uuid NOT NULL REFERENCES core.quality_characteristic(id),
            material_id             uuid NOT NULL REFERENCES core.material(id),
            equipment_id            uuid REFERENCES core.equipment(id),     -- the line its results were made on; NULL: results without one
            status                  text NOT NULL DEFAULT 'open' CHECK (status IN ('open','closed')),
            rules                   smallint[] NOT NULL,                    -- every rule broken while it was open
            breaks                  integer NOT NULL DEFAULT 1 CHECK (breaks >= 1),  -- results that broke a rule while it was open
            first_sample_id         uuid NOT NULL,                          -- ts.quality_sample (no FK into history)
            first_sample_at         timestamptz NOT NULL,
            last_sample_id          uuid NOT NULL,
            last_sample_at          timestamptz NOT NULL,
            raised_at               timestamptz NOT NULL,
            closed_at               timestamptz,
            closed_by               text,
            cause_reason_code_id    uuid REFERENCES core.reason_code(id),   -- kind spc_cause
            action                  text CHECK (length(action) <= 1000),
            created_at              timestamptz NOT NULL DEFAULT now(),
            updated_at              timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT spc_alert_closed_ck CHECK ((status = 'closed') = (closed_at IS NOT NULL AND cause_reason_code_id IS NOT NULL AND action IS NOT NULL))
        );
        -- One open alert per chart: further breaks are added to it.
        CREATE UNIQUE INDEX spc_alert_open_uq ON core.spc_alert (site_id, characteristic_id, material_id, (coalesce(equipment_id, '00000000-0000-0000-0000-000000000000'::uuid)))
            WHERE status = 'open';
        CREATE INDEX spc_alert_site_ix ON core.spc_alert (site_id, raised_at DESC);
        CREATE TABLE ts.spc_violation (
            id                  uuid NOT NULL DEFAULT uuidv7(),
            occurred_at         timestamptz NOT NULL,                       -- when the breaking result was sampled
            site_id             uuid NOT NULL REFERENCES core.equipment(id),
            characteristic_id   uuid NOT NULL REFERENCES core.quality_characteristic(id),
            material_id         uuid NOT NULL REFERENCES core.material(id),
            equipment_id        uuid REFERENCES core.equipment(id),
            sample_id           uuid NOT NULL,                              -- ts.quality_sample, at occurred_at
            rules               smallint[] NOT NULL,
            alert_id            uuid NOT NULL REFERENCES core.spc_alert(id),
            recorded_at         timestamptz NOT NULL,
            PRIMARY KEY (id, occurred_at)
        ) PARTITION BY RANGE (occurred_at);
        CREATE INDEX spc_violation_alert_ix ON ts.spc_violation (alert_id, occurred_at);
        CREATE INDEX ON ts.spc_violation (characteristic_id, occurred_at);
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.spc_alert TO pulaski_app;
        GRANT SELECT, INSERT ON ts.spc_violation TO pulaski_app;
    END IF;
END $$;
