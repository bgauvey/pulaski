-- 0076 · Maintenance requests (#127, D-115): a request for maintenance to look at a line or machine, raised on its own or from one of its stops,
-- started and closed with what was done. It changes as it is worked and is referred to (by a CMMS too), so it lives in core (D-010).
-- Permissions maintenance.request (raise) and maintenance.work (start, close); a Maintenance role for new and existing databases.
-- Re-runnable; no BEGIN/COMMIT.

CREATE SEQUENCE IF NOT EXISTS core.maintenance_request_no;

CREATE TABLE IF NOT EXISTS core.maintenance_request (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    equipment_id            uuid NOT NULL REFERENCES core.equipment(id),
    number                  text NOT NULL CONSTRAINT maintenance_request_number_uq UNIQUE,   -- MR-000042, from the sequence
    status                  text NOT NULL DEFAULT 'open' CONSTRAINT maintenance_request_status_ck CHECK (status IN ('open', 'in_progress', 'closed')),
    priority                text NOT NULL DEFAULT 'normal' CONSTRAINT maintenance_request_priority_ck CHECK (priority IN ('low', 'normal', 'high')),
    description             text NOT NULL,
    raised_at               timestamptz NOT NULL,
    raised_by               text NOT NULL,                              -- as on evt.event
    raised_from             text,                                       -- the operator station's code, when raised there
    downtime_id             uuid,                                       -- the stop it was raised from: ts.downtime (id, started_at), no FK (D-010)
    downtime_started_at     timestamptz,
    started_at              timestamptz,
    started_by              text,
    closed_at               timestamptz,
    closed_by               text,
    resolution              text,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT maintenance_request_started_ck CHECK ((status = 'open') = (started_at IS NULL)),
    CONSTRAINT maintenance_request_closed_ck CHECK ((status = 'closed') = (closed_at IS NOT NULL AND resolution IS NOT NULL)),
    CONSTRAINT maintenance_request_stop_ck CHECK ((downtime_id IS NULL) = (downtime_started_at IS NULL))
);

-- One request still open per stop: asking again from the same stop is the same request.
CREATE UNIQUE INDEX IF NOT EXISTS maintenance_request_stop_uq ON core.maintenance_request (downtime_id) WHERE status <> 'closed' AND downtime_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS maintenance_request_site_ix ON core.maintenance_request (site_id, raised_at DESC);
CREATE INDEX IF NOT EXISTS maintenance_request_equipment_ix ON core.maintenance_request (equipment_id, raised_at DESC);

INSERT INTO sec.permission (code, description) VALUES
    ('maintenance.request', 'Request maintenance for a line or machine, on its own or from a stop'),
    ('maintenance.work', 'Work maintenance requests: start them and close them with what was done')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role (code, name, description, is_system) VALUES
    ('maintenance', 'Maintenance', 'Works maintenance requests: starts and closes them, raises them, codes machine stops and answers andon calls. Sees the floor, machines and equipment.', true)
ON CONFLICT (code) DO NOTHING;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES ('operator', 'maintenance.request'), ('supervisor', 'maintenance.request'), ('supervisor', 'maintenance.work'),
             ('maintenance', 'overview.view'), ('maintenance', 'floor.view'), ('maintenance', 'machines.view'), ('maintenance', 'equipment.view'),
             ('maintenance', 'downtime.code'), ('maintenance', 'andon.respond'), ('maintenance', 'handover.write'),
             ('maintenance', 'maintenance.request'), ('maintenance', 'maintenance.work')) AS p(role, code)
  ON p.role = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- Descriptions change only where an administrator hasn't rewritten them.
UPDATE sec.role SET description = 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops, records quality results, calls for help, requests maintenance.'
WHERE code = 'operator' AND description = 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops, records quality results, calls for help.';
UPDATE sec.role SET description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes, enters, splits and merges machine stops; records quality results; calls for and answers andon calls; requests and works maintenance. Doesn''t correct inventory.'
WHERE code = 'supervisor' AND description = 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; reverses production reports; records and approves labor; codes, enters, splits and merges machine stops; records quality results; calls for and answers andon calls. Doesn''t correct inventory.';

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.maintenance_request TO pulaski_app;
        GRANT USAGE ON SEQUENCE core.maintenance_request_no TO pulaski_app;
    END IF;
END $$;
