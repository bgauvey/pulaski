-- 0009 · Batches and partial batches (D-036): a material may be made in batches of a fixed size; a
-- batch cut short when a line stops is a partial batch, and one of an exact-batch material waits for
-- approval. Re-runnable. Runs in one transaction under db/migrate.sh; no BEGIN/COMMIT here.

ALTER TABLE core.material
    ADD COLUMN IF NOT EXISTS batch_size numeric(18,6) CHECK (batch_size > 0),
    ADD COLUMN IF NOT EXISTS batch_tolerance_pct numeric(5,2) NOT NULL DEFAULT 2 CHECK (batch_tolerance_pct >= 0 AND batch_tolerance_pct <= 50),
    ADD COLUMN IF NOT EXISTS batch_rule text CHECK (batch_rule IN ('exact','near'));

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'material_batch_ck') THEN
        ALTER TABLE core.material ADD CONSTRAINT material_batch_ck CHECK ((batch_size IS NULL) = (batch_rule IS NULL));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS core.partial_batch (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    job_id              uuid NOT NULL REFERENCES core.job(id),
    run_id              uuid NOT NULL REFERENCES core.job_run(id),
    lot_id              uuid NOT NULL REFERENCES core.lot(id),
    quantity            core.qty NOT NULL CHECK (quantity > 0),
    uom_id              uuid NOT NULL REFERENCES core.uom(id),
    batch_size          numeric(18,6) NOT NULL CHECK (batch_size > 0),
    batch_rule          text NOT NULL CHECK (batch_rule IN ('exact','near')),
    status              text NOT NULL CHECK (status IN ('accepted','pending','approved','rejected')),
    occurred_at         timestamptz NOT NULL,
    reported_by         text,
    decided_at          timestamptz,
    decided_by          text,
    decision_comment    text CHECK (length(decision_comment) <= 1000),
    scrap_reason_code_id uuid REFERENCES core.reason_code(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT partial_batch_run_uq UNIQUE (run_id),
    CONSTRAINT partial_batch_decided_ck CHECK ((status IN ('approved','rejected')) = (decided_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS partial_batch_pending_ix ON core.partial_batch (status) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS partial_batch_job_ix ON core.partial_batch (job_id);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.partial_batch TO pulaski_app;
    END IF;
END $$;
