-- 0068 · B2MML delivery feeds (D-102): a feed sends CloudEvents (as before) or, for every event that changes a production job's totals, the
-- job's B2MML SyncProductionPerformance. Fixed when the feed is added, like its kind. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.delivery_feed ADD COLUMN IF NOT EXISTS format text NOT NULL DEFAULT 'cloudevents';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'delivery_feed_format_ck') THEN
        ALTER TABLE core.delivery_feed ADD CONSTRAINT delivery_feed_format_ck CHECK (format IN ('cloudevents','b2mml'));
    END IF;
END $$;
