-- 0033 · Batch sizes per line (D-065): a capability may give the line its own batch size for the material. Re-runnable; no
-- BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

SET LOCAL client_min_messages = warning;

ALTER TABLE core.equipment_capability ADD COLUMN IF NOT EXISTS batch_size core.qty CHECK (batch_size > 0);
