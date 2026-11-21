-- 0070 · Andon calls (#125, D-108): call types per site, each with an escalation time, and calls raised for a line, acknowledged by the
-- responder on arrival and resolved. A call changes as it is answered and is referred to, so it lives in core (D-010), like SPC alerts.
-- Permissions andon.call (raise) and andon.respond (acknowledge, resolve), granted to the roles that came with the database.
-- Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.andon_type (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    code                    text NOT NULL,                              -- upper case; fixed once made
    name                    text NOT NULL,
    escalate_after_minutes  integer NOT NULL CONSTRAINT andon_type_escalate_ck CHECK (escalate_after_minutes BETWEEN 1 AND 480),
    sort_order              integer NOT NULL DEFAULT 0,
    is_active               boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT andon_type_uq UNIQUE (site_id, code)
);

CREATE TABLE IF NOT EXISTS core.andon_call (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    line_id             uuid NOT NULL REFERENCES core.equipment(id),
    andon_type_id       uuid NOT NULL REFERENCES core.andon_type(id),
    status              text NOT NULL DEFAULT 'open' CONSTRAINT andon_call_status_ck CHECK (status IN ('open', 'acknowledged', 'resolved')),
    note                text,
    raised_at           timestamptz NOT NULL,
    raised_by           text NOT NULL,                              -- as on evt.event
    raised_from         text,                                       -- the operator station's code, when raised there
    acknowledged_at     timestamptz,
    acknowledged_by     text,
    resolved_at         timestamptz,
    resolved_by         text,
    resolution          text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT andon_call_acknowledged_ck CHECK ((status = 'open') = (acknowledged_at IS NULL)),
    CONSTRAINT andon_call_resolved_ck CHECK ((status = 'resolved') = (resolved_at IS NOT NULL))
);

-- One open call per line and type: calling again while it waits is the same call.
CREATE UNIQUE INDEX IF NOT EXISTS andon_call_open_uq ON core.andon_call (line_id, andon_type_id) WHERE status <> 'resolved';
CREATE INDEX IF NOT EXISTS andon_call_site_ix ON core.andon_call (site_id, raised_at DESC);

INSERT INTO sec.permission (code, description) VALUES
    ('andon.call', 'Call for help from a line: maintenance, quality, materials or a supervisor'),
    ('andon.respond', 'Answer andon calls: acknowledge them on arrival and resolve them')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES ('operator', 'andon.call'), ('supervisor', 'andon.call'), ('supervisor', 'andon.respond'), ('quality', 'andon.respond')) AS p(role, code)
  ON p.role = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- Descriptions change only where an administrator hasn't rewritten them.
UPDATE sec.role SET description = 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops, records quality results, calls for help.'
WHERE code = 'operator' AND description = 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops, records quality results.';
UPDATE sec.role SET description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes, enters, splits and merges machine stops; records quality results; calls for and answers andon calls. Doesn''t correct inventory.'
WHERE code = 'supervisor' AND description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes, enters, splits and merges machine stops; records quality results. Doesn''t correct inventory.';
UPDATE sec.role SET description = 'Holds and releases lots, corrects inventory, decides on partial batches, keeps lot attributes and certificates, maintains specifications, records quality results, excludes them and rejects lots on quality, answers andon calls.'
WHERE code = 'quality' AND description = 'Holds and releases lots, corrects inventory, decides on partial batches, keeps lot attributes and certificates, maintains specifications, records quality results, excludes them and rejects lots on quality.';

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.andon_type TO pulaski_app;
        GRANT SELECT, INSERT, UPDATE ON core.andon_call TO pulaski_app;
    END IF;
END $$;
