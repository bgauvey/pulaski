-- 0039 · Stop adjustment follow-ups (#68): changing a stop's times (retime), undoing an adjustment (restore) and machine data trimmed around a
-- stop entered by hand (trim) are new stops replacing old ones, like splits and merges (D-069). A stop removed or undone keeps why in
-- superseded_note. Re-runnable; no BEGIN/COMMIT.

SET LOCAL client_min_messages = warning;

ALTER TABLE ts.downtime ADD COLUMN IF NOT EXISTS superseded_note text;

ALTER TABLE ts.downtime
    DROP CONSTRAINT IF EXISTS downtime_source_ck,
    ADD CONSTRAINT downtime_source_ck CHECK (source IN ('machine', 'manual', 'split', 'merge', 'retime', 'restore', 'trim')),
    DROP CONSTRAINT IF EXISTS downtime_replaces_ck,
    ADD CONSTRAINT downtime_replaces_ck CHECK ((source IN ('split', 'merge', 'retime', 'restore', 'trim')) = (replaces_ids IS NOT NULL));
