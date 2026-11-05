-- 0055 · Delivery feeds added on screen, MQTT feeds and retention (#74, D-088): a feed is a row with a code, a name and a kind (webhook or
-- mqtt); delivered rows older than Retention:DeliveredDays are cleared by evt.purge_delivered. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.delivery_feed DROP CONSTRAINT IF EXISTS delivery_feed_feed_check;
ALTER TABLE core.delivery_feed ADD COLUMN IF NOT EXISTS name text NOT NULL DEFAULT '';
ALTER TABLE core.delivery_feed ADD COLUMN IF NOT EXISTS kind text NOT NULL DEFAULT 'webhook' CHECK (kind IN ('webhook','mqtt'));
ALTER TABLE core.delivery_feed ADD COLUMN IF NOT EXISTS topic text;
ALTER TABLE core.delivery_feed ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'delivery_feed_code_ck') THEN
        ALTER TABLE core.delivery_feed ADD CONSTRAINT delivery_feed_code_ck CHECK (feed ~ '^[a-z][a-z0-9_-]{1,29}$');
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS delivery_delivered_ix ON evt.delivery (delivered_at) WHERE status = 'delivered';

CREATE OR REPLACE FUNCTION evt.purge_delivered(p_keep_days integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_deleted integer;
BEGIN
    IF p_keep_days IS NULL OR p_keep_days < 7 THEN
        RAISE EXCEPTION 'purge_delivered: keep at least 7 days, not %', p_keep_days;
    END IF;

    DELETE FROM evt.delivery WHERE status = 'delivered' AND delivered_at < now() - make_interval(days => p_keep_days);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    RETURN v_deleted;
END $$;

REVOKE ALL ON FUNCTION evt.purge_delivered(integer) FROM PUBLIC;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT EXECUTE ON FUNCTION evt.purge_delivered(integer) TO pulaski_app;
    END IF;
END $$;
