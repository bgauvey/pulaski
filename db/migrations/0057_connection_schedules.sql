-- 0057 · Scheduled connections and people sources (#71, D-090): a connection imports materials or people at a site, runs on demand or on a
-- schedule, and records every import in ts.connection_run. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS target text NOT NULL DEFAULT 'materials';
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS site_id uuid REFERENCES core.equipment(id);
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS schedule_every_hours smallint;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS schedule_daily_at time[];
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS next_run_at timestamptz;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS create_classes boolean NOT NULL DEFAULT false;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS switch_off_missing boolean NOT NULL DEFAULT false;
ALTER TABLE core.material_connection ADD COLUMN IF NOT EXISTS last_run_outcome text;

-- The kinds gain scim; dropped and made again, so a re-run ends with the same definition.
ALTER TABLE core.material_connection DROP CONSTRAINT IF EXISTS material_connection_kind_ck;
ALTER TABLE core.material_connection ADD CONSTRAINT material_connection_kind_ck CHECK (
    kind IN ('odata','rest','soap','database','scim')
    AND (kind = 'database') = (base_url IS NULL)
    AND (kind = 'database') = (query IS NOT NULL)
    AND (kind = 'soap') = (request_body IS NOT NULL));

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'material_connection_target_ck') THEN
        ALTER TABLE core.material_connection ADD CONSTRAINT material_connection_target_ck CHECK (
            target IN ('materials','people')
            AND (target = 'people') = (site_id IS NOT NULL)
            AND (kind <> 'scim' OR target = 'people'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'material_connection_schedule_ck') THEN
        ALTER TABLE core.material_connection ADD CONSTRAINT material_connection_schedule_ck CHECK (
            (schedule_every_hours IS NULL OR schedule_every_hours BETWEEN 1 AND 168)
            AND (schedule_daily_at IS NULL OR cardinality(schedule_daily_at) BETWEEN 1 AND 4)
            AND NOT (schedule_every_hours IS NOT NULL AND schedule_daily_at IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'material_connection_outcome_ck') THEN
        ALTER TABLE core.material_connection ADD CONSTRAINT material_connection_outcome_ck CHECK (last_run_outcome IN ('applied','not_applied','failed'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS material_connection_due_ix ON core.material_connection (next_run_at) WHERE next_run_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS ts.connection_run (
    id                  uuid NOT NULL DEFAULT uuidv7(),
    started_at          timestamptz NOT NULL,
    finished_at         timestamptz NOT NULL,
    connection_id       uuid NOT NULL REFERENCES core.material_connection(id),
    trigger             text NOT NULL CHECK (trigger IN ('manual','schedule')),
    outcome             text NOT NULL CHECK (outcome IN ('applied','not_applied','failed')),
    created_count       integer NOT NULL DEFAULT 0,
    updated_count       integer NOT NULL DEFAULT 0,
    unchanged_count     integer NOT NULL DEFAULT 0,
    error_count         integer NOT NULL DEFAULT 0,
    summary             text NOT NULL CHECK (length(summary) <= 2000),
    actor               text NOT NULL,
    PRIMARY KEY (id, started_at)
) PARTITION BY RANGE (started_at);
CREATE INDEX IF NOT EXISTS connection_run_connection_ix ON ts.connection_run (connection_id, started_at DESC);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT ON ts.connection_run TO pulaski_app;
    END IF;
END $$;
