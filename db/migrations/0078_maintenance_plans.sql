-- 0078 · Preventive maintenance plans (#127d, D-115): every so many run hours or machine counts, a request for a line or machine is raised by
-- itself. A plan is referred to by its requests, so it lives in core and is switched off rather than deleted (D-010).
-- Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.maintenance_plan (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    equipment_id        uuid NOT NULL REFERENCES core.equipment(id),
    name                text NOT NULL,                              -- what is done; the requests' description
    meter               text NOT NULL CONSTRAINT maintenance_plan_meter_ck CHECK (meter IN ('run_hours', 'counts')),   -- fixed once made
    interval_value      numeric(18,3) NOT NULL CONSTRAINT maintenance_plan_interval_ck CHECK (interval_value > 0),
    priority            text NOT NULL DEFAULT 'normal' CONSTRAINT maintenance_plan_priority_ck CHECK (priority IN ('low', 'normal', 'high')),
    is_active           boolean NOT NULL DEFAULT true,
    started_at          timestamptz NOT NULL,                       -- the meter counts from here, or from its last request's close if later
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by          text,
    updated_at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS maintenance_plan_site_ix ON core.maintenance_plan (site_id);

ALTER TABLE core.maintenance_request ADD COLUMN IF NOT EXISTS maintenance_plan_id uuid REFERENCES core.maintenance_plan(id);   -- raised by this plan

-- One request of a plan open at a time: the next interval counts from when it is closed.
CREATE UNIQUE INDEX IF NOT EXISTS maintenance_request_plan_uq ON core.maintenance_request (maintenance_plan_id) WHERE status <> 'closed' AND maintenance_plan_id IS NOT NULL;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.maintenance_plan TO pulaski_app;
    END IF;
END $$;
