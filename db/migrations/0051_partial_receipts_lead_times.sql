-- 0051 · Partial receipts and lead times (#83, D-083): an expected receipt counts what has arrived against it and stays open for the
-- rest until fully received or closed as complete; each lot received against it is recorded. Materials may carry a lead time in days.
-- Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.expected_receipt ADD COLUMN IF NOT EXISTS received_qty core.qty NOT NULL DEFAULT 0;
ALTER TABLE core.expected_receipt DROP CONSTRAINT IF EXISTS expected_receipt_received_ck;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'expected_receipt_received_qty_ck') THEN
        ALTER TABLE core.expected_receipt ADD CONSTRAINT expected_receipt_received_qty_ck CHECK (received_qty >= 0);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS core.expected_receipt_lot (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    expected_receipt_id uuid NOT NULL REFERENCES core.expected_receipt(id),
    lot_id              uuid NOT NULL REFERENCES core.lot(id),
    quantity            core.qty NOT NULL CHECK (quantity > 0),
    received_at         timestamptz NOT NULL,
    received_by         text NOT NULL
);
CREATE INDEX IF NOT EXISTS expected_receipt_lot_receipt_ix ON core.expected_receipt_lot (expected_receipt_id);

-- Receipts closed before partial receipts were whole: everything expected arrived, in the one lot named.
UPDATE core.expected_receipt SET received_qty = quantity WHERE status = 'received' AND received_qty = 0;
INSERT INTO core.expected_receipt_lot (expected_receipt_id, lot_id, quantity, received_at, received_by)
SELECT r.id, r.received_lot_id, r.quantity, r.updated_at, r.created_by
FROM core.expected_receipt r
WHERE r.received_lot_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM core.expected_receipt_lot x WHERE x.expected_receipt_id = r.id);

ALTER TABLE core.material ADD COLUMN IF NOT EXISTS lead_time_days integer;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'material_lead_time_ck') THEN
        ALTER TABLE core.material ADD CONSTRAINT material_lead_time_ck CHECK (lead_time_days BETWEEN 1 AND 3650);
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.expected_receipt_lot TO pulaski_app;
    END IF;
END $$;
