-- 0063 · Planning rates from run history without stops, refreshed on a schedule (#70, D-098). Re-runnable; no BEGIN/COMMIT.

-- A calculated rate may now come from run history with the stops inside runs taken out.
ALTER TABLE core.run_standard DROP CONSTRAINT IF EXISTS run_standard_calculation_basis_check;
ALTER TABLE core.run_standard ADD CONSTRAINT run_standard_calculation_basis_check
    CHECK (calculation_basis IN ('ideal_rate','run_history','run_history_net'));

-- How a site keeps planning rates from run history current (#70, D-098): stops out by default, a scheduled recalculation that proposes
-- rates, and opting in to small changes without review. No row: the defaults below.
CREATE TABLE IF NOT EXISTS core.rate_refresh_settings (
    site_id                 uuid PRIMARY KEY REFERENCES core.equipment(id),
    take_out_stops          boolean NOT NULL DEFAULT false,    -- the review's default, and the scheduled recalculation's basis
    is_scheduled            boolean NOT NULL DEFAULT false,
    every_days              smallint NOT NULL DEFAULT 7 CHECK (every_days BETWEEN 1 AND 90),
    window_days             smallint NOT NULL DEFAULT 90 CHECK (window_days BETWEEN 7 AND 365),
    threshold_pct           numeric(5,2) NOT NULL DEFAULT 5 CHECK (threshold_pct BETWEEN 0 AND 100),     -- smaller changes aren't proposed
    auto_accept             boolean NOT NULL DEFAULT false,
    auto_accept_limit_pct   numeric(5,2) NOT NULL DEFAULT 10 CHECK (auto_accept_limit_pct > 0 AND auto_accept_limit_pct <= 100),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT rate_refresh_auto_ck CHECK (NOT auto_accept OR is_scheduled)
);

-- When the scheduled recalculation last ran for a site; its own row, so running it never changes the settings' version.
CREATE TABLE IF NOT EXISTS core.rate_refresh (
    site_id         uuid PRIMARY KEY REFERENCES core.equipment(id),
    refreshed_at    timestamptz NOT NULL
);

-- A planning rate the scheduled recalculation proposes (#70, D-098), with what it was worked out from, until it is accepted (by a
-- planner, or by the site's opt-in), rejected or replaced by a newer one. One pending per run standard.
CREATE TABLE IF NOT EXISTS core.run_standard_proposal (
    id                          uuid PRIMARY KEY DEFAULT uuidv7(),
    run_standard_id             uuid NOT NULL REFERENCES core.run_standard(id),
    proposed_rate_per_hour      numeric(18,6) NOT NULL CHECK (proposed_rate_per_hour > 0),
    uom_id                      uuid NOT NULL REFERENCES core.uom(id),
    calculation_basis           text NOT NULL CHECK (calculation_basis IN ('run_history','run_history_net')),
    window_days                 smallint NOT NULL,
    window_from                 timestamptz NOT NULL,
    window_to                   timestamptz NOT NULL,
    used_runs                   integer NOT NULL,
    used_hours                  numeric(12,2) NOT NULL,
    used_good_qty               numeric(18,6) NOT NULL,
    stopped_hours               numeric(12,2) NOT NULL DEFAULT 0,
    calculated_rate_per_hour    numeric(18,6) NOT NULL,         -- the run standard's when proposed
    effective_rate_per_hour     numeric(18,6) NOT NULL,
    review_reason               text,                           -- why it waits for review
    status                      text NOT NULL DEFAULT 'pending'
                                CHECK (status IN ('pending','accepted','auto_accepted','rejected','superseded')),
    proposed_at                 timestamptz NOT NULL,
    decided_at                  timestamptz,
    decided_by                  text,
    decision_note               text CHECK (length(decision_note) <= 1000),
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT run_standard_proposal_decided_ck CHECK ((status = 'pending') = (decided_at IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS run_standard_proposal_pending_uq ON core.run_standard_proposal (run_standard_id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS run_standard_proposal_proposed_ix ON core.run_standard_proposal (proposed_at);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.rate_refresh_settings, core.rate_refresh, core.run_standard_proposal TO pulaski_app;
    END IF;
END $$;
