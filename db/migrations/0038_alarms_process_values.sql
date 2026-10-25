-- 0038 · Alarms, warnings and process values (#26). Alarm and warning tags carry the active code (PackML Admin.Alarm[].ID, 0 = none):
-- ingest raises and clears ts.equipment_alarm rows per tag; a machine may have several. Process values go to ts.process_value, kept
-- Retention:ProcessValueDays (90 by default) through ts.drop_old_process_values. Re-runnable; no BEGIN/COMMIT.

SET LOCAL client_min_messages = warning;

-- The tag that raised an alarm: a machine with several alarm words clears each on its own.
ALTER TABLE ts.equipment_alarm ADD COLUMN IF NOT EXISTS equipment_tag_id uuid REFERENCES core.equipment_tag(id);
CREATE INDEX IF NOT EXISTS equipment_alarm_open_ix ON ts.equipment_alarm (equipment_tag_id) WHERE cleared_at IS NULL;

-- One active tag per role on a piece of equipment, except process values, alarms and warnings, which may have several.
DROP INDEX IF EXISTS core.equipment_tag_role_uq;
CREATE UNIQUE INDEX IF NOT EXISTS equipment_tag_role_uq ON core.equipment_tag (equipment_id, tag_role)
    WHERE is_active AND tag_role NOT IN ('process_value', 'alarm', 'warning');

-- Drops the monthly ts.process_value partitions that end more than p_keep_days ago; returns how many. SECURITY DEFINER, like
-- ts.ensure_partitions: dropping a partition needs ownership, which pulaski_app doesn't have. Only this table, never less than a week.
CREATE OR REPLACE FUNCTION ts.drop_old_process_values(p_keep_days integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_part   regclass;
    v_bound  text;
    v_end    timestamptz;
    v_dropped integer := 0;
BEGIN
    IF p_keep_days IS NULL OR p_keep_days < 7 THEN
        RAISE EXCEPTION 'drop_old_process_values: keep at least 7 days, not %', p_keep_days;
    END IF;

    FOR v_part, v_bound IN
        SELECT c.oid::regclass, pg_get_expr(c.relpartbound, c.oid)
        FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'ts.process_value'::regclass
    LOOP
        -- FOR VALUES FROM ('…') TO ('…'): the upper bound is the second quoted value.
        v_end := (regexp_match(v_bound, 'TO \(''([^'']+)''\)'))[1]::timestamptz;
        IF v_end IS NOT NULL AND v_end <= now() - make_interval(days => p_keep_days) THEN
            EXECUTE format('DROP TABLE %s', v_part);
            v_dropped := v_dropped + 1;
        END IF;
    END LOOP;
    RETURN v_dropped;
END $$;

REVOKE ALL ON FUNCTION ts.drop_old_process_values(integer) FROM PUBLIC;
