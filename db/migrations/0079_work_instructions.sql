-- 0079 · Work instructions (#126a, D-116): documents of a site written and released in versions (text and attached files), each shown for
-- process operations, materials or lines. Referred to by jobs and the checklists answered from them, so they live in core (D-010).
-- Permissions instructions.view and instructions.manage. Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.work_instruction (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id         uuid NOT NULL REFERENCES core.equipment(id),
    code            text NOT NULL,                                  -- upper case; fixed once made
    title           text NOT NULL,
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT work_instruction_code_uq UNIQUE (site_id, code)
);

CREATE TABLE IF NOT EXISTS core.work_instruction_version (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    instruction_id  uuid NOT NULL REFERENCES core.work_instruction(id),
    version_number  integer NOT NULL CHECK (version_number > 0),
    status          text NOT NULL DEFAULT 'draft' CONSTRAINT work_instruction_version_status_ck CHECK (status IN ('draft', 'released', 'obsolete')),
    body            text NOT NULL DEFAULT '',                       -- the text: headings, numbered steps, bold (#126a)
    released_at     timestamptz,
    released_by     text,
    note            text,                                           -- why this version, given on release
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT work_instruction_version_uq UNIQUE (instruction_id, version_number),
    CONSTRAINT work_instruction_version_released_ck CHECK ((status = 'draft') = (released_at IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS work_instruction_version_draft_uq ON core.work_instruction_version (instruction_id) WHERE status = 'draft';
CREATE UNIQUE INDEX IF NOT EXISTS work_instruction_version_released_uq ON core.work_instruction_version (instruction_id) WHERE status = 'released';

-- A file's bytes, written once; the versions that carry the file point at them, so a new draft copies no bytes.
CREATE TABLE IF NOT EXISTS core.work_instruction_content (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    sha256          text NOT NULL,
    size_bytes      integer NOT NULL CHECK (size_bytes > 0),
    content         bytea NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS core.work_instruction_file (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    version_id      uuid NOT NULL REFERENCES core.work_instruction_version(id),
    content_id      uuid NOT NULL REFERENCES core.work_instruction_content(id),
    file_name       text NOT NULL,
    content_type    text NOT NULL CONSTRAINT work_instruction_file_type_ck CHECK (content_type IN ('application/pdf', 'image/png', 'image/jpeg')),
    sort_order      integer NOT NULL DEFAULT 0,
    removed_at      timestamptz,                                    -- taken out of a draft; never deleted
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS work_instruction_file_version_ix ON core.work_instruction_file (version_id);

CREATE TABLE IF NOT EXISTS core.work_instruction_target (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    instruction_id      uuid NOT NULL REFERENCES core.work_instruction(id),
    kind                text NOT NULL CONSTRAINT work_instruction_target_kind_ck CHECK (kind IN ('operation', 'material', 'line')),
    process_id          uuid REFERENCES core.process(id),           -- an operation: the process and its operation's number, whatever the version
    operation_number    integer CHECK (operation_number > 0),
    material_id         uuid REFERENCES core.material(id),
    equipment_id        uuid REFERENCES core.equipment(id),         -- a line
    is_active           boolean NOT NULL DEFAULT true,              -- ended, never deleted
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT work_instruction_target_shape_ck CHECK (
        (kind = 'operation' AND process_id IS NOT NULL AND operation_number IS NOT NULL AND material_id IS NULL AND equipment_id IS NULL)
        OR (kind = 'material' AND material_id IS NOT NULL AND process_id IS NULL AND operation_number IS NULL AND equipment_id IS NULL)
        OR (kind = 'line' AND equipment_id IS NOT NULL AND process_id IS NULL AND operation_number IS NULL AND material_id IS NULL))
);
CREATE INDEX IF NOT EXISTS work_instruction_target_instruction_ix ON core.work_instruction_target (instruction_id);
CREATE INDEX IF NOT EXISTS work_instruction_target_process_ix ON core.work_instruction_target (process_id, operation_number) WHERE is_active;
CREATE INDEX IF NOT EXISTS work_instruction_target_material_ix ON core.work_instruction_target (material_id) WHERE is_active;
CREATE INDEX IF NOT EXISTS work_instruction_target_line_ix ON core.work_instruction_target (equipment_id) WHERE is_active;

INSERT INTO sec.permission (code, description) VALUES
    ('instructions.view', 'See work instructions and their versions'),
    ('instructions.manage', 'Maintain work instructions: write, attach and release them')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES ('planner', 'instructions.view'), ('supervisor', 'instructions.view'), ('operator', 'instructions.view'),
             ('quality', 'instructions.view'), ('quality', 'instructions.manage'), ('manager', 'instructions.view'), ('auditor', 'instructions.view'),
             ('process_engineer', 'instructions.view'), ('process_engineer', 'instructions.manage'), ('maintenance', 'instructions.view')) AS p(role, code)
  ON p.role = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.work_instruction, core.work_instruction_version, core.work_instruction_file,
            core.work_instruction_target TO pulaski_app;
        GRANT SELECT, INSERT ON core.work_instruction_content TO pulaski_app;
    END IF;
END $$;
