-- 0030 · Reason trees (D-062): groups that hold reasons, the class rule that lets a group have none, and the tree function the
-- pickers and the Reasons screen read. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

SET LOCAL client_min_messages = warning;

-- A utilization reason has a class (D-050); a group (D-062) and every other kind have none.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'reason_code_class_ck' AND conrelid = 'core.reason_code'::regclass) THEN
        ALTER TABLE core.reason_code DROP CONSTRAINT IF EXISTS reason_code_utilization_ck;
        ALTER TABLE core.reason_code ADD CONSTRAINT reason_code_class_ck CHECK (CASE WHEN NOT is_selectable THEN utilization_class IS NULL
                                                                                     ELSE (reason_kind = 'utilization') = (utilization_class IS NOT NULL) END);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS reason_code_parent_ix ON core.reason_code (parent_id) WHERE parent_id IS NOT NULL;

-- Every reason with its place in its tree (D-062): how deep, the names of the groups above it (top first, joined by ' › ') and
-- a key that sorts a kind's reasons in tree order (each level by sort order, then name). Small tables; worked out on read.
CREATE OR REPLACE FUNCTION core.reason_tree()
RETURNS TABLE (id uuid, depth integer, group_path text, sort_key text[])
LANGUAGE sql STABLE AS $$
    WITH RECURSIVE t (id, name, depth, group_path, sort_key) AS (
        SELECT r.id, r.name, 0, NULL::text, ARRAY[lpad(r.sort_order::text, 5, '0') || lower(r.name) || ' ' || r.code]
        FROM core.reason_code r
        WHERE r.parent_id IS NULL
        UNION ALL
        SELECT c.id, c.name, t.depth + 1, coalesce(t.group_path || ' › ', '') || t.name,
               t.sort_key || (lpad(c.sort_order::text, 5, '0') || lower(c.name) || ' ' || c.code)
        FROM t JOIN core.reason_code c ON c.parent_id = t.id
    ) CYCLE id SET is_cycle USING visited
    SELECT id, depth, group_path, sort_key FROM t WHERE NOT is_cycle
$$;
