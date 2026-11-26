-- Approval before release (#138, D-122): per site, processes, BOMs and specifications may need a second person's approval to be released.

CREATE TABLE IF NOT EXISTS core.release_approval_settings (
    site_id         uuid PRIMARY KEY REFERENCES core.equipment(id),
    processes       boolean NOT NULL DEFAULT false,
    boms            boolean NOT NULL DEFAULT false,
    specs           boolean NOT NULL DEFAULT false,
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS core.release_request (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    kind            text NOT NULL CONSTRAINT release_request_kind_ck CHECK (kind IN ('process', 'bom', 'spec')),
    document_id     uuid NOT NULL,                                  -- the process, BOM or specification (by kind)
    version_id      uuid NOT NULL,                                  -- its version submitted
    site_ids        uuid[] NOT NULL,                                -- the sites whose setting asks for approval
    status          text NOT NULL DEFAULT 'pending' CONSTRAINT release_request_status_ck CHECK (status IN ('pending', 'approved', 'returned', 'withdrawn')),
    submitted_by    text NOT NULL,
    submitted_at    timestamptz NOT NULL,
    note            text,
    decided_by      text,
    decided_at      timestamptz,
    decision_note   text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT release_request_decided_ck CHECK ((status = 'pending') = (decided_at IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS release_request_pending_uq ON core.release_request (version_id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS release_request_document_ix ON core.release_request (kind, document_id, submitted_at DESC);

INSERT INTO sec.permission (code, description) VALUES
    ('releases.approve', 'Approve or send back processes, BOMs and specifications submitted for release')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES ('quality', 'releases.approve'), ('process_engineer', 'releases.approve')) AS p(role, code) ON p.role = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.release_approval_settings, core.release_request TO pulaski_app;
    END IF;
END $$;
