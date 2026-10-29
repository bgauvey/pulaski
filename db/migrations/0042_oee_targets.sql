-- 0042 · OEE targets (D-073, #67): availability, performance, quality and OEE targets for a site (its default) or a piece of equipment,
-- in periods from a production day until ended; never deleted. Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.oee_target (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    equipment_id        uuid REFERENCES core.equipment(id),        -- null: the site's default
    effective_from      date NOT NULL,
    effective_to        date,                                       -- first day no longer in force; null while open
    availability_pct    numeric(4,1) CHECK (availability_pct > 0 AND availability_pct <= 100),
    performance_pct     numeric(4,1) CHECK (performance_pct > 0 AND performance_pct <= 100),
    quality_pct         numeric(4,1) CHECK (quality_pct > 0 AND quality_pct <= 100),
    oee_pct             numeric(4,1) CHECK (oee_pct > 0 AND oee_pct <= 100),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT oee_target_period_ck CHECK (effective_to IS NULL OR effective_to > effective_from),
    CONSTRAINT oee_target_values_ck CHECK (num_nonnulls(availability_pct, performance_pct, quality_pct, oee_pct) > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS oee_target_scope_from_uq ON core.oee_target (site_id, coalesce(equipment_id, site_id), effective_from);
