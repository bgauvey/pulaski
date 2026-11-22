-- 0074 · Shift handover (#124, D-113): notes and open items per line and shift (the line's shift when written, D-038, else the site-local
-- day). Open items stay open, carried from shift to shift, until someone closes them; nothing is deleted. Permission handover.write, granted to
-- the roles that came with the database. Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.handover_entry (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    line_id             uuid NOT NULL REFERENCES core.equipment(id),
    production_date     date NOT NULL,                                  -- the shift's production day, else the site-local day
    shift_code          text,                                           -- null without a shift (no schedule, or between shifts)
    kind                text NOT NULL CONSTRAINT handover_entry_kind_ck CHECK (kind IN ('note', 'open_item')),
    body                text NOT NULL CONSTRAINT handover_entry_body_ck CHECK (length(body) BETWEEN 1 AND 1000),
    written_at          timestamptz NOT NULL,
    written_by          text NOT NULL,                                  -- as on evt.event
    closed_at           timestamptz,
    closed_by           text,
    closing_note        text CONSTRAINT handover_entry_closing_ck CHECK (length(closing_note) <= 1000),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT handover_entry_closed_ck CHECK ((closed_at IS NULL) = (closed_by IS NULL) AND (kind = 'open_item' OR closed_at IS NULL))
);
CREATE INDEX IF NOT EXISTS handover_entry_line_ix ON core.handover_entry (line_id, production_date DESC, written_at DESC);
CREATE INDEX IF NOT EXISTS handover_entry_open_ix ON core.handover_entry (line_id) WHERE kind = 'open_item' AND closed_at IS NULL;

INSERT INTO sec.permission (code, description) VALUES
    ('handover.write', 'Write shift handover notes and open items for lines, and close open items')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'handover.write' FROM sec.role r WHERE r.code IN ('operator', 'supervisor', 'quality') AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.handover_entry TO pulaski_app;
    END IF;
END $$;
