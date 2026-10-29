-- 0041 · Daily OEE rollups (#66, D-072): each site's production days worked out once and kept, so reports longer than 31 days
-- add them up. Replaces the planned hourly ts.oee_rollup, which nothing wrote: dropped only while it is still that partitioned draft.
-- Re-runnable; no BEGIN/COMMIT.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_class WHERE oid = to_regclass('ts.oee_rollup') AND relkind = 'p') THEN
        DROP TABLE ts.oee_rollup;
    END IF;
END $$;

-- Daily OEE rollups (#66, D-072). Not partitioned: a row per site and day, or per equipment and day, is small, and a re-roll updates in place.
-- A site's generation moves on whenever a setting that decides how past time counts changes; every day rolled at an older one rolls again.
CREATE TABLE IF NOT EXISTS ts.oee_rollup_site (
    site_id             uuid PRIMARY KEY REFERENCES core.equipment(id),
    generation          bigint NOT NULL DEFAULT 1,
    changed_at          timestamptz NOT NULL DEFAULT now()
);

-- A site's production day: when it was rolled, at which generation, and whether a late change to its history has marked it since.
CREATE TABLE IF NOT EXISTS ts.oee_rollup_day (
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    production_date     date NOT NULL,
    rolled_generation   bigint,                                     -- null: never rolled
    rolled_at           timestamptz,
    dirty_at            timestamptz,                                -- set by a write to the day's history; cleared by rolling it
    PRIMARY KEY (site_id, production_date)
);

-- Each piece of equipment's working windows of a production day as the OEE report adds them up (OeeWindowFigures as JSON): time by use
-- and reason, output by product, unrated runs. An empty array for equipment with none that day.
CREATE TABLE IF NOT EXISTS ts.oee_rollup (
    equipment_id        uuid NOT NULL REFERENCES core.equipment(id),
    production_date     date NOT NULL,
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    rolled_at           timestamptz NOT NULL,
    windows             jsonb NOT NULL,
    PRIMARY KEY (equipment_id, production_date)
);
