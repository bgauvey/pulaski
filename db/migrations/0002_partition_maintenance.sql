-- 0002 · Partition maintenance (D-028): one function creates the months ahead for every
-- partitioned history table, so the API can keep them ready without owning the tables.
-- Re-runnable. Runs in one transaction under db/migrate.sh; no BEGIN/COMMIT here.

-- Creates the current month and the next p_months - 1 for every range-partitioned table in ts
-- and evt, and returns how many partitions it created (0 when all were there). SECURITY
-- DEFINER: it runs as the schema owner, because creating a partition needs ownership of its
-- parent, which pulaski_app doesn't have. db/app-role.sql grants EXECUTE to pulaski_app only.
CREATE OR REPLACE FUNCTION ts.ensure_partitions(p_months integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_parent regclass;
    v_before integer;
    v_after  integer;
BEGIN
    IF p_months IS NULL OR p_months < 1 OR p_months > 24 THEN
        RAISE EXCEPTION 'ensure_partitions: months must be between 1 and 24, not %', p_months;
    END IF;

    SELECT count(*) INTO v_before FROM pg_class WHERE relispartition AND relkind = 'r';
    FOR v_parent IN
        SELECT pt.partrelid::regclass
        FROM pg_partitioned_table pt
        JOIN pg_class c ON c.oid = pt.partrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('ts', 'evt') AND pt.partstrat = 'r'
        ORDER BY 1
    LOOP
        PERFORM ts.create_month_partitions(v_parent, current_date, p_months);
    END LOOP;
    SELECT count(*) INTO v_after FROM pg_class WHERE relispartition AND relkind = 'r';
    RETURN v_after - v_before;
END $$;

REVOKE ALL ON FUNCTION ts.ensure_partitions(integer) FROM PUBLIC;
