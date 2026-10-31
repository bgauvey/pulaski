-- 0044 · Line batch tolerance (#85, D-075): a capability may give the line its own batch tolerance, beside its own batch size (D-065).
-- Null keeps the material's. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.equipment_capability
    ADD COLUMN IF NOT EXISTS batch_tolerance_pct numeric(5,2) CHECK (batch_tolerance_pct >= 0 AND batch_tolerance_pct <= 50);
