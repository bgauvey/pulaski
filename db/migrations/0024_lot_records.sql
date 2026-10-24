-- 0024 · Lot attributes and certificates (D-056): typed attribute definitions and values beside the lot, and lot documents
-- (stored files or references) linked to the lots they apply to. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one
-- transaction). db/app-role.sql grants the new tables and keeps document content insert-only.

-- Lot attributes (D-056): typed properties a site records on its lots, such as melt index or moisture, kept beside the lot so
-- its identity and movement history never change. A definition is offered for every material, or only for materials in the
-- classes it is applied to; its code and type are fixed once created, and it is switched off, never deleted.
CREATE TABLE IF NOT EXISTS core.lot_attribute (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    code                text NOT NULL,                              -- upper case; fixed once created
    name                text NOT NULL,
    data_type           text NOT NULL CHECK (data_type IN ('text','number','date','boolean','choice')),
    uom_id              uuid REFERENCES core.uom(id),               -- a number's unit, for display
    choices             text[] NOT NULL DEFAULT '{}',               -- the values a choice may take
    applies_to_all      boolean NOT NULL DEFAULT true,              -- false: only materials in the applied classes
    sort_order          integer NOT NULL DEFAULT 0,
    is_active           boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT lot_attribute_uq UNIQUE (site_id, code),
    CONSTRAINT lot_attribute_uom_ck CHECK (uom_id IS NULL OR data_type = 'number'),
    CONSTRAINT lot_attribute_choices_ck CHECK ((data_type = 'choice') = (cardinality(choices) > 0))
);

-- The material classes an attribute is offered for, when not for all; taken off with is_applied, never deleted.
CREATE TABLE IF NOT EXISTS core.lot_attribute_class (
    attribute_id        uuid NOT NULL REFERENCES core.lot_attribute(id),
    material_class_id   uuid NOT NULL REFERENCES core.material_class(id),
    is_applied          boolean NOT NULL DEFAULT true,
    PRIMARY KEY (attribute_id, material_class_id)
);

-- A lot's value of an attribute, in the column of its type; all null once cleared. Every change is an event with before
-- and after (D-007), so the row holds only the current value.
CREATE TABLE IF NOT EXISTS core.lot_attribute_value (
    lot_id              uuid NOT NULL REFERENCES core.lot(id),
    attribute_id        uuid NOT NULL REFERENCES core.lot_attribute(id),
    value_text          text,                                       -- text and choice
    value_number        numeric,
    value_date          date,
    value_boolean       boolean,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          text NOT NULL,
    PRIMARY KEY (lot_id, attribute_id),
    CONSTRAINT lot_attribute_value_one_ck CHECK (num_nonnulls(value_text, value_number, value_date, value_boolean) <= 1)
);
CREATE INDEX IF NOT EXISTS lot_attribute_value_text_ix ON core.lot_attribute_value (attribute_id, value_text) WHERE value_text IS NOT NULL;
CREATE INDEX IF NOT EXISTS lot_attribute_value_number_ix ON core.lot_attribute_value (attribute_id, value_number) WHERE value_number IS NOT NULL;
CREATE INDEX IF NOT EXISTS lot_attribute_value_date_ix ON core.lot_attribute_value (attribute_id, value_date) WHERE value_date IS NOT NULL;

-- Certificates and other lot documents (D-056): a stored file (PDF, PNG or JPEG), a reference to one kept elsewhere (a document
-- number, a URL), or both. Never deleted: a wrong one is withdrawn with a reason and stays, file included.
CREATE TABLE IF NOT EXISTS core.lot_document (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    kind                text NOT NULL CHECK (kind IN ('coa','coc','test_report','sds','other')),
    title               text NOT NULL,
    document_number     text,
    issuer              text,                                       -- such as the supplier or lab
    issued_on           date,
    url                 text,
    file_name           text,
    content_type        text CHECK (content_type IN ('application/pdf','image/png','image/jpeg')),
    size_bytes          integer CHECK (size_bytes > 0),
    sha256              bytea,
    status              text NOT NULL DEFAULT 'current' CHECK (status IN ('current','withdrawn')),
    withdrawn_at        timestamptz,
    withdrawn_by        text,
    withdraw_reason     text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by          text NOT NULL,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT lot_document_file_ck CHECK (num_nulls(file_name, content_type, size_bytes, sha256) IN (0, 4)),
    CONSTRAINT lot_document_source_ck CHECK (file_name IS NOT NULL OR url IS NOT NULL OR document_number IS NOT NULL),
    CONSTRAINT lot_document_withdrawn_ck CHECK ((status = 'withdrawn') = (withdrawn_at IS NOT NULL AND withdrawn_by IS NOT NULL AND withdraw_reason IS NOT NULL))
);

-- A stored document's bytes, apart so lists never read them. Written once: the application may insert, never update or delete.
CREATE TABLE IF NOT EXISTS core.lot_document_content (
    document_id         uuid PRIMARY KEY REFERENCES core.lot_document(id),
    content             bytea NOT NULL
);

-- The lots a document applies to: the one it was attached to, and the lots split from or merged into it, which carry it on.
CREATE TABLE IF NOT EXISTS core.lot_document_link (
    document_id         uuid NOT NULL REFERENCES core.lot_document(id),
    lot_id              uuid NOT NULL REFERENCES core.lot(id),
    via                 text NOT NULL CHECK (via IN ('attached','split','merge')),
    from_lot_id         uuid REFERENCES core.lot(id),               -- the lot it came from, for split and merge
    linked_at           timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (document_id, lot_id),
    CONSTRAINT lot_document_link_from_ck CHECK ((via = 'attached') = (from_lot_id IS NULL))
);
CREATE INDEX IF NOT EXISTS lot_document_link_lot_ix ON core.lot_document_link (lot_id);

INSERT INTO sec.permission (code, description) VALUES
    ('lot_records.manage', 'Record lot attributes and attach or withdraw certificates'),
    ('lot_attributes.manage', 'Maintain lot attribute definitions')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM sec.role r
JOIN (VALUES ('quality', 'lot_records.manage'), ('quality', 'lot_attributes.manage'), ('manager', 'lot_attributes.manage')) AS p(role_code, code)
  ON p.role_code = r.code
WHERE r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- The starting roles' descriptions, unless an administrator has already changed them.
UPDATE sec.role SET description = 'Holds and releases lots, corrects inventory, decides on partial batches, and keeps lot attributes and certificates.'
WHERE code = 'quality' AND is_system AND description = 'Holds and releases lots, corrects inventory and decides on partial batches.';
UPDATE sec.role SET description = 'Views everything; reopens jobs, closes work orders, approves labor, maintains reason codes, shift schedules, people, lot attributes and the site''s settings.'
WHERE code = 'manager' AND is_system
  AND description = 'Views everything; reopens jobs, closes work orders, approves labor, maintains reason codes, shift schedules, people and the site''s settings.';
