-- 0003 · Movement journal (D-029): a BRIN index so a time-range read of ts.lot_event stays cheap.
-- Re-runnable. Runs in one transaction under db/migrate.sh; no BEGIN/COMMIT here.

CREATE INDEX IF NOT EXISTS lot_event_occurred_at_brin ON ts.lot_event USING brin (occurred_at);
