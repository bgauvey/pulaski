-- 0026 · Expected receipts (D-058): supply on its way, for the shortage check. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it
-- in one transaction). db/app-role.sql grants the new table.

-- Supply known to be on its way (D-058): a purchase or transfer the ERP or a person says will arrive, so the shortage check can
-- show it beside stock. Not a purchase order: Pulaski doesn't place, price or plan them. Receiving a lot against one closes it.
CREATE TABLE IF NOT EXISTS core.expected_receipt (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    material_id         uuid NOT NULL REFERENCES core.material(id),
    quantity            core.qty NOT NULL CHECK (quantity > 0),
    uom_id              uuid NOT NULL REFERENCES core.uom(id),
    expected_at         timestamptz NOT NULL,
    reference           text,                                       -- a purchase order, transfer or ASN number
    supplier            text,
    note                text,
    status              text NOT NULL DEFAULT 'open' CHECK (status IN ('open','received','cancelled')),
    received_lot_id     uuid REFERENCES core.lot(id),               -- the lot received against it
    external_system     text,                                       -- the ERP that sent it; with the key, matched by every import
    external_key        text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by          text NOT NULL,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT expected_receipt_external_ck CHECK ((external_system IS NULL) = (external_key IS NULL)),
    CONSTRAINT expected_receipt_received_ck CHECK (received_lot_id IS NULL OR status = 'received')
);
CREATE UNIQUE INDEX IF NOT EXISTS expected_receipt_external_uq ON core.expected_receipt (external_system, external_key) WHERE external_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS expected_receipt_open_ix ON core.expected_receipt (site_id, material_id, expected_at) WHERE status = 'open';
