-- 0034 · Work orders from the ERP (D-066): what the ERP sent, how its quantity was made whole batches, changes waiting for a planner,
-- and the rule for quantities that aren't whole batches (per site, per material). Re-runnable; no BEGIN/COMMIT.

SET LOCAL client_min_messages = warning;

ALTER TABLE core.material ADD COLUMN IF NOT EXISTS erp_batch_rule text CHECK (erp_batch_rule IN ('reject','round_up','round_down','hold'));
ALTER TABLE core.scheduling_policy ADD COLUMN IF NOT EXISTS erp_batch_rule text NOT NULL DEFAULT 'reject'
    CHECK (erp_batch_rule IN ('reject','round_up','round_down','hold'));

ALTER TABLE core.work_order ADD COLUMN IF NOT EXISTS erp_pending_change jsonb;
ALTER TABLE core.work_order ADD COLUMN IF NOT EXISTS erp_qty core.qty;
ALTER TABLE core.work_order ADD COLUMN IF NOT EXISTS erp_uom_id uuid REFERENCES core.uom(id);
ALTER TABLE core.work_order ADD COLUMN IF NOT EXISTS batch_adjustment text CHECK (batch_adjustment IN ('rounded_up','rounded_down','held'));
ALTER TABLE core.work_order ADD COLUMN IF NOT EXISTS quantity_held boolean NOT NULL DEFAULT false;
ALTER TABLE core.work_order ADD COLUMN IF NOT EXISTS imported_at timestamptz;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'wo_erp_pending_ck' AND conrelid = 'core.work_order'::regclass) THEN
        UPDATE core.work_order SET erp_change_pending = false WHERE erp_change_pending AND erp_pending_change IS NULL;
        ALTER TABLE core.work_order ADD CONSTRAINT wo_erp_pending_ck CHECK (erp_change_pending = (erp_pending_change IS NOT NULL));
    END IF;
END $$;
