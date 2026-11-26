-- Reporting views, piece (b) (#132, D-129): inventory and genealogy, labor, quality. Same promise as 0088: they change only by adding columns,
-- and pulaski_report reads them and nothing else. See design/reporting.md.

-- ---- Inventory and genealogy ----

-- Lots as they are now (D-027), in the material's base unit. A balance (is_bulk, D-031) has no lot number: it is named by its location.
CREATE OR REPLACE VIEW report.lot AS
SELECT l.id AS lot_id, l.site_id, l.lot_number, coalesce(l.lot_number, '@' || coalesce(loc.code, '?')) AS label, l.material_id, l.status,
       l.quantity, l.uom_id, l.location_equipment_id AS location_id, l.container_id, l.is_bulk, l.origin, l.supplier_lot, l.expires_at,
       l.produced_by_job_id, l.produced_by_run_id, l.created_at, l.updated_at,
       l.status IN ('available', 'on_hold', 'quarantine') AS is_on_hand
FROM core.lot l
LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id;

-- Stock on hand now, by site, material, location and status, in the material's base unit.
CREATE OR REPLACE VIEW report.stock AS
SELECT l.site_id, l.material_id, l.location_equipment_id AS location_id, l.status, l.uom_id, count(*) AS lot_count, sum(l.quantity) AS quantity,
       min(l.expires_at) AS first_expires_at
FROM core.lot l
WHERE l.status IN ('available', 'on_hold', 'quarantine') AND l.quantity > 0
GROUP BY l.site_id, l.material_id, l.location_equipment_id, l.status, l.uom_id;

-- Every lot movement (D-027, D-057). recorded_at is when it was entered; effective_at when it counts in stock (earlier when back-dated): stock
-- at a time is the sum of qty_delta with effective_at up to then. A reversal (D-063) is a movement of its own (reverses_movement_id); the
-- movement it undoes is_reversed, so sum all rows, or leave out both.
CREATE OR REPLACE VIEW report.lot_movement AS
SELECT e.id AS movement_id, e.occurred_at AS recorded_at, e.effective_at, (e.effective_at AT TIME ZONE s.time_zone)::date AS site_local_date,
       e.lot_id, l.site_id, l.material_id, e.event_kind AS kind, e.qty_delta, e.qty_after, e.uom_id,
       e.from_location_id, e.to_location_id, e.related_lot_id, e.container_id, e.job_id, e.run_id, e.from_status, e.to_status,
       e.reason_code_id AS reason_id, e.reference, e.reverses_event_id AS reverses_movement_id,
       EXISTS (SELECT 1 FROM ts.lot_event r WHERE r.lot_id = e.lot_id AND r.reverses_event_id = e.id) AS is_reversed,
       e.report_id, e.auto_source, e.actor_id, e.comment
FROM ts.lot_event e
JOIN core.lot l ON l.id = e.lot_id
JOIN core.equipment s ON s.id = l.site_id;

-- Genealogy as it stands (D-027, D-078): which lots went into which, at job level (declared) or as picked; superseded links left out.
-- Pairing a used lot to each made lot by time happens in the app on read and isn't here.
CREATE OR REPLACE VIEW report.lot_genealogy AS
SELECT g.id AS link_id, g.parent_lot_id, g.child_lot_id, g.link_basis, g.quantity, g.uom_id, g.job_id, g.run_id, g.effective_at, g.derived_at
FROM core.lot_genealogy g
WHERE g.superseded_at IS NULL;

-- ---- Labor ----

CREATE OR REPLACE VIEW report.person AS
SELECT p.id AS person_id, p.site_id, p.employee_number, p.display_name, p.crew_id, c.code AS crew_code, c.name AS crew_name, p.origin,
       p.external_system, p.external_key, p.is_active
FROM core.person p
LEFT JOIN core.crew c ON c.id = p.crew_id;

-- Labor sessions as they stand (D-040): a correction replaces the session it corrects, and a void one removes it. worked_hours = the time
-- outside the person's breaks (D-047) × its share, as the app works it out; approved once its shift's labor is approved.
CREATE OR REPLACE VIEW report.labor_session AS
WITH sessions AS (
    SELECT s.* FROM core.labor_session s
    WHERE NOT s.is_void AND NOT EXISTS (SELECT 1 FROM core.labor_session c WHERE c.corrects_session_id = s.id)
), breaks AS (
    SELECT b.* FROM core.labor_break b
    WHERE NOT b.is_void AND NOT EXISTS (SELECT 1 FROM core.labor_break c WHERE c.corrects_break_id = b.id)
)
SELECT s.id AS session_id, s.site_id, s.person_id, s.equipment_id, s.job_id, s.labor_type, s.reason_code_id AS reason_id,
       s.started_at, s.ended_at, s.allocation_pct, s.production_date, s.shift_code, s.source,
       extract(epoch FROM (coalesce(s.ended_at, now()) - s.started_at)) / 3600 AS elapsed_hours,
       coalesce(br.hours, 0) AS break_hours,
       (extract(epoch FROM (coalesce(s.ended_at, now()) - s.started_at)) / 3600 - coalesce(br.hours, 0)) * s.allocation_pct / 100 AS worked_hours,
       s.ended_at IS NULL AS is_open,
       EXISTS (SELECT 1 FROM core.labor_approval a WHERE a.site_id = s.site_id AND a.production_date = s.production_date
               AND a.shift_schedule_id IS NOT DISTINCT FROM s.shift_schedule_id AND a.shift_code IS NOT DISTINCT FROM s.shift_code) AS is_approved
FROM sessions s
LEFT JOIN LATERAL (
    SELECT sum(extract(epoch FROM (least(coalesce(b.ended_at, now()), coalesce(s.ended_at, now())) - greatest(b.started_at, s.started_at))) / 3600) AS hours
    FROM breaks b
    WHERE b.person_id = s.person_id AND b.started_at < coalesce(s.ended_at, now()) AND coalesce(b.ended_at, now()) > s.started_at
) br ON true;

-- ---- Quality ----

CREATE OR REPLACE VIEW report.characteristic AS
SELECT q.id AS characteristic_id, q.code, q.name, q.data_kind, q.chart_type, q.subgroup_size, q.uom_id, q.decimals, q.is_active
FROM core.quality_characteristic q;

-- Quality results (D-085) with the specification limits they were checked against; excluded ones are kept, flagged.
CREATE OR REPLACE VIEW report.quality_result AS
SELECT r.id AS result_id, r.occurred_at AS sampled_at, (r.occurred_at AT TIME ZONE s.time_zone)::date AS site_local_date, r.recorded_at,
       r.site_id, r.characteristic_id, r.material_id, r.equipment_id, r.job_id, r.run_id, r.lot_id,
       r.subgroup_size, r.mean, r.range, r.std_dev, r.measured_values, r.inspected_count, r.defect_count,
       i.lsl, i.target, i.usl, i.max_defects, r.conforms, r.source, r.recorded_by,
       r.is_excluded, r.exclusion_reason_id
FROM ts.quality_sample r
JOIN core.equipment s ON s.id = r.site_id
LEFT JOIN core.material_spec_item i ON i.id = r.spec_item_id;

CREATE OR REPLACE VIEW report.spc_alert AS
SELECT a.id AS alert_id, a.site_id, a.characteristic_id, a.material_id, a.equipment_id, a.status, a.rules, a.breaks, a.raised_at, a.closed_at,
       a.closed_by, a.cause_reason_code_id AS cause_reason_id, a.action,
       extract(epoch FROM (coalesce(a.closed_at, now()) - a.raised_at)) / 3600 AS open_hours
FROM core.spc_alert a;

-- Nonconformance reports (D-124), with their actions counted.
CREATE OR REPLACE VIEW report.nonconformance AS
SELECT n.id AS ncr_id, n.site_id, n.number, n.title, n.severity, n.status, n.source_kind, n.raised_at, n.raised_by, n.root_cause,
       n.disposition, n.closed_at, n.closed_by,
       extract(epoch FROM (coalesce(n.closed_at, now()) - n.raised_at)) / 86400 AS open_days,
       count(a.id) AS action_count,
       count(a.id) FILTER (WHERE a.status = 'open') AS open_actions,
       count(a.id) FILTER (WHERE a.status = 'open' AND a.due_on < (now() AT TIME ZONE coalesce(s.time_zone, 'UTC'))::date) AS overdue_actions
FROM core.nonconformance n
JOIN core.equipment s ON s.id = n.site_id
LEFT JOIN core.nonconformance_action a ON a.ncr_id = n.id
GROUP BY n.id, s.time_zone;

CREATE OR REPLACE VIEW report.nonconformance_lot AS
SELECT l.ncr_id, l.lot_id FROM core.nonconformance_lot l;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_report') THEN
        GRANT SELECT ON ALL TABLES IN SCHEMA report TO pulaski_report;
    END IF;
END $$;
