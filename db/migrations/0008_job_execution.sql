-- 0008 · Job execution and production reporting (D-035): when a job completed, who reported a
-- count and when, per-site tolerances for the rate and consumption checks, and the app's grant on
-- the counts. Re-runnable. Runs in one transaction under db/migrate.sh; no BEGIN/COMMIT here.

ALTER TABLE core.job ADD COLUMN IF NOT EXISTS completed_at timestamptz;

ALTER TABLE ts.production_count
    ADD COLUMN IF NOT EXISTS job_id uuid,
    ADD COLUMN IF NOT EXISTS recorded_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS actor_id text,
    ADD COLUMN IF NOT EXISTS comment text CHECK (length(comment) <= 1000);

CREATE INDEX IF NOT EXISTS production_count_job_ix ON ts.production_count (job_id) WHERE job_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS core.execution_policy (
    site_id                     uuid PRIMARY KEY REFERENCES core.equipment(id),
    rate_tolerance_pct          numeric(6,1) NOT NULL DEFAULT 110 CHECK (rate_tolerance_pct >= 100 AND rate_tolerance_pct <= 1000),
    consumption_tolerance_pct   numeric(6,1) NOT NULL DEFAULT 110 CHECK (consumption_tolerance_pct >= 100 AND consumption_tolerance_pct <= 1000),
    updated_at                  timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.execution_policy TO pulaski_app;
        GRANT SELECT, INSERT ON ts.production_count TO pulaski_app;
    END IF;
END $$;
