-- 0049 · Containers inside containers (#76, D-081): a container may sit inside another (a box on a pallet); moving the outer one moves
-- everything inside. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.container ADD COLUMN IF NOT EXISTS parent_container_id uuid REFERENCES core.container(id);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'container_parent_ck') THEN
        ALTER TABLE core.container ADD CONSTRAINT container_parent_ck CHECK (parent_container_id <> id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS container_parent_ix ON core.container (parent_container_id) WHERE parent_container_id IS NOT NULL;
