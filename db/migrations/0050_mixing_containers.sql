-- 0050 · Containers that mix lots (#93, D-082): a container may mix what goes into it (a gaylord of partial bags, a regrind tote); lots
-- packed into it join its balance of their material, which is kept per container rather than per location. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.container ADD COLUMN IF NOT EXISTS mixes_lots boolean NOT NULL DEFAULT false;
ALTER TABLE core.container ADD COLUMN IF NOT EXISTS flow_model text NOT NULL DEFAULT 'mixed';
ALTER TABLE core.container ADD COLUMN IF NOT EXISTS min_residence interval;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'container_flow_model_ck') THEN
        ALTER TABLE core.container ADD CONSTRAINT container_flow_model_ck CHECK (flow_model IN ('mixed','fifo'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'container_min_residence_ck') THEN
        ALTER TABLE core.container ADD CONSTRAINT container_min_residence_ck CHECK (min_residence IS NULL OR min_residence >= interval '0');
    END IF;
END $$;

-- A location's balance is one per material there, outside containers; a mixing container's is one per material in it.
DROP INDEX IF EXISTS core.lot_bulk_uq;
CREATE UNIQUE INDEX IF NOT EXISTS lot_bulk_uq ON core.lot (material_id, location_equipment_id) WHERE is_bulk AND container_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS lot_bulk_container_uq ON core.lot (material_id, container_id) WHERE is_bulk AND container_id IS NOT NULL;
