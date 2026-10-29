-- 0040 · Reason groups in reports (#86): core.reason_tree() also gives the ids of the groups above each reason (top first), so reports roll up
-- to a group level; core.reason_code_uses() names where a reason is referred to, so only a reason nothing refers to can become a group.
-- Re-runnable; no BEGIN/COMMIT.

SET LOCAL client_min_messages = warning;

-- Its result gains a column, so it is dropped and made again.
DROP FUNCTION IF EXISTS core.reason_tree();
CREATE FUNCTION core.reason_tree()
RETURNS TABLE (id uuid, depth integer, group_path text, sort_key text[], group_ids uuid[])
LANGUAGE sql STABLE AS $$
    WITH RECURSIVE t (id, name, depth, group_path, sort_key, group_ids) AS (
        SELECT r.id, r.name, 0, NULL::text, ARRAY[lpad(r.sort_order::text, 5, '0') || lower(r.name) || ' ' || r.code], ARRAY[]::uuid[]
        FROM core.reason_code r
        WHERE r.parent_id IS NULL
        UNION ALL
        SELECT c.id, c.name, t.depth + 1, coalesce(t.group_path || ' › ', '') || t.name,
               t.sort_key || (lpad(c.sort_order::text, 5, '0') || lower(c.name) || ' ' || c.code), t.group_ids || t.id
        FROM t JOIN core.reason_code c ON c.parent_id = t.id
    ) CYCLE id SET is_cycle USING visited
    SELECT id, depth, group_path, sort_key, group_ids FROM t WHERE NOT is_cycle
$$;

-- Every table and column that refers to the reason, apart from reasons filed under it: found from the foreign keys, so a new table that
-- refers to reasons is covered. Reads only; whether a reason may become a group is decided in the application. It runs with its owner's
-- rights because the app role can't read every such table (ts.quality_sample), and it tells only whether the reason is used there.
CREATE OR REPLACE FUNCTION core.reason_code_uses(p_id uuid)
RETURNS TABLE (table_name text, column_name text)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_table regclass;
    v_column text;
    v_used boolean;
BEGIN
    FOR v_table, v_column IN
        SELECT c.conrelid::regclass, a.attname
        FROM pg_constraint c
        JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
        WHERE c.contype = 'f' AND c.confrelid = 'core.reason_code'::regclass AND cardinality(c.conkey) = 1
          AND c.conparentid = 0                       -- not the copies on each partition
          AND NOT (c.conrelid = 'core.reason_code'::regclass AND a.attname = 'parent_id')
        ORDER BY 1, 2
    LOOP
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE %I = $1)', v_table, v_column) INTO v_used USING p_id;
        IF v_used THEN
            table_name := v_table::text;
            column_name := v_column;
            RETURN NEXT;
        END IF;
    END LOOP;
END $$;
