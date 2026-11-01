-- 0045 · Reversal details (#79, D-077): a correction keeps the lot details it replaced, so it can be reversed. Older corrections have none
-- and stay irreversible. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE ts.lot_event ADD COLUMN IF NOT EXISTS details jsonb;
