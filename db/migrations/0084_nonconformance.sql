-- Nonconformance reports (#133, D-124): raised from anything that flags, investigated, acted on and closed with a disposition.

CREATE SEQUENCE IF NOT EXISTS core.nonconformance_no;

CREATE TABLE IF NOT EXISTS core.nonconformance (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id         uuid NOT NULL REFERENCES core.equipment(id),
    number          text NOT NULL UNIQUE,                               -- NCR-000042
    title           text NOT NULL,
    description     text NOT NULL DEFAULT '',
    severity        text NOT NULL CONSTRAINT nonconformance_severity_ck CHECK (severity IN ('minor', 'major', 'critical')),
    status          text NOT NULL DEFAULT 'open' CONSTRAINT nonconformance_status_ck CHECK (status IN ('open', 'investigating', 'acting', 'closed')),
    source_kind     text NOT NULL CONSTRAINT nonconformance_source_ck CHECK (source_kind IN ('manual', 'lot_hold', 'lot_rejection', 'quality_result', 'spc_alert', 'checklist', 'complaint')),
    source_id       uuid,                                               -- the lot, result, alert or checklist answer (no FK: by kind)
    source_at       timestamptz,                                        -- a result's sampled time (its partition key)
    owner_user_id   uuid REFERENCES sec.app_user(id),
    raised_by       text NOT NULL,
    raised_at       timestamptz NOT NULL,
    root_cause      text,
    whys            text[] NOT NULL DEFAULT '{}',
    disposition     text CONSTRAINT nonconformance_disposition_ck CHECK (disposition IN ('use_as_is', 'rework', 'scrap', 'return_to_supplier', 'not_applicable')),
    closing_note    text,
    closed_at       timestamptz,
    closed_by       text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT nonconformance_closed_ck CHECK ((status = 'closed') = (closed_at IS NOT NULL AND disposition IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS nonconformance_site_ix ON core.nonconformance (site_id, status, raised_at DESC);

CREATE TABLE IF NOT EXISTS core.nonconformance_action (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    ncr_id          uuid NOT NULL REFERENCES core.nonconformance(id),
    kind            text NOT NULL CONSTRAINT nonconformance_action_kind_ck CHECK (kind IN ('corrective', 'preventive')),
    description     text NOT NULL,
    owner_user_id   uuid REFERENCES sec.app_user(id),
    due_on          date,
    status          text NOT NULL DEFAULT 'open' CONSTRAINT nonconformance_action_status_ck CHECK (status IN ('open', 'done', 'cancelled')),
    done_at         timestamptz,
    done_by         text,
    note            text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      text NOT NULL,
    CONSTRAINT nonconformance_action_done_ck CHECK ((status = 'open') = (done_at IS NULL))
);
CREATE INDEX IF NOT EXISTS nonconformance_action_ncr_ix ON core.nonconformance_action (ncr_id);
CREATE INDEX IF NOT EXISTS nonconformance_action_owner_ix ON core.nonconformance_action (owner_user_id) WHERE status = 'open';

CREATE TABLE IF NOT EXISTS core.nonconformance_lot (
    ncr_id          uuid NOT NULL REFERENCES core.nonconformance(id),
    lot_id          uuid NOT NULL REFERENCES core.lot(id),
    PRIMARY KEY (ncr_id, lot_id)
);
CREATE INDEX IF NOT EXISTS nonconformance_lot_lot_ix ON core.nonconformance_lot (lot_id);

CREATE TABLE IF NOT EXISTS core.nonconformance_run (
    ncr_id          uuid NOT NULL REFERENCES core.nonconformance(id),
    run_id          uuid NOT NULL REFERENCES core.job_run(id),
    PRIMARY KEY (ncr_id, run_id)
);

INSERT INTO sec.permission (code, description) VALUES
    ('ncr.raise', 'Raise nonconformance reports'),
    ('ncr.manage', 'Work nonconformance reports: investigate, add actions and close them')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES ('quality', 'ncr.raise'), ('quality', 'ncr.manage'), ('supervisor', 'ncr.raise'), ('operator', 'ncr.raise')) AS p(role, code) ON p.role = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.nonconformance, core.nonconformance_action, core.nonconformance_lot, core.nonconformance_run TO pulaski_app;
        GRANT USAGE ON SEQUENCE core.nonconformance_no TO pulaski_app;
    END IF;
END $$;
