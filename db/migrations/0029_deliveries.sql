-- 0029 · Outbound deliveries (D-061): the queue the ERP webhook relay delivers from, and its permissions. Re-runnable; no
-- BEGIN/COMMIT (db/migrate.sh runs it in one transaction). db/app-role.sql grants the table.

-- Quiet the "already exists, skipping" notices on a re-run; only for this transaction.
SET LOCAL client_min_messages = warning;

-- Outbound deliveries (D-061): each event a sink takes, copied from evt.event in commit order, and how its delivery went. The relay
-- holds an event's later ones for the same subject while it is undelivered, so a receiver never sees them out of order. Rows point
-- into the partitioned log by (event_id, event_recorded_at), without a foreign key.
CREATE TABLE IF NOT EXISTS evt.delivery (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    sink                text NOT NULL,                              -- 'webhook:erp'
    event_id            uuid NOT NULL,
    event_recorded_at   timestamptz NOT NULL,
    tx_id               xid8 NOT NULL,
    position            bigint NOT NULL,
    event_type          text NOT NULL,
    subject             uuid NOT NULL,
    status              text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','delivered','failed','skipped')),
    attempts            integer NOT NULL DEFAULT 0,
    next_attempt_at     timestamptz NOT NULL DEFAULT now(),
    last_attempt_at     timestamptz,
    last_status_code    integer,
    last_error          text,
    delivered_at        timestamptz,
    resolved_by         text,                                       -- who retried or skipped it
    resolve_note        text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT delivery_event_uq UNIQUE (sink, event_id)
);
CREATE INDEX IF NOT EXISTS delivery_due_ix ON evt.delivery (sink, next_attempt_at) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS delivery_subject_ix ON evt.delivery (sink, subject, tx_id, position) WHERE status IN ('pending','failed');
CREATE INDEX IF NOT EXISTS delivery_status_ix ON evt.delivery (sink, status, created_at);

INSERT INTO sec.permission (code, description) VALUES
    ('integrations.view', 'See outbound deliveries to the ERP and how they went'),
    ('integrations.manage', 'Retry or skip failed deliveries to the ERP')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'integrations.view' FROM sec.role r WHERE r.code IN ('manager', 'auditor') AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
