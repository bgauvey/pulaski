-- Reporting views (#132, D-129): a stable, documented layer for BI tools (Power BI, a warehouse), read through the pulaski_report role.
-- Internal tables change as Pulaski grows; these views keep their names, columns and meaning, and change only by adding columns (a
-- breaking change gets a new view name). Times are timestamptz (UTC on the wire); site_local_date is the site's calendar day.
-- Views run with their owner's rights, so pulaski_report needs no access to core, ts or evt. See design/reporting.md.

CREATE SCHEMA IF NOT EXISTS report;

-- ---- Dimensions ----

CREATE OR REPLACE VIEW report.site AS
SELECT e.id AS site_id, e.code, e.name, e.time_zone, e.is_active
FROM core.equipment e
WHERE e.level = 'site';

-- Every piece of equipment with its site, and the area and line above it (a line is the nearest one, itself included, that runs jobs).
CREATE OR REPLACE VIEW report.equipment AS
SELECT e.id AS equipment_id, e.site_id, e.code, e.name, e.level, e.equipment_role AS role, e.parent_id,
       p.code AS parent_code,
       area.id AS area_id, area.code AS area_code,
       line.id AS line_id, line.code AS line_code,
       e.can_run_job, e.is_schedulable, e.holds_inventory, e.tracks_state, e.is_active
FROM core.equipment e
LEFT JOIN core.equipment p ON p.id = e.parent_id
LEFT JOIN LATERAL (
    SELECT a.id, a.code FROM core.equipment_closure c JOIN core.equipment a ON a.id = c.ancestor_id
    WHERE c.descendant_id = e.id AND a.level = 'area' ORDER BY c.depth LIMIT 1) area ON true
LEFT JOIN LATERAL (
    SELECT l.id, l.code FROM core.equipment_closure c JOIN core.equipment l ON l.id = c.ancestor_id
    WHERE c.descendant_id = e.id AND l.can_run_job ORDER BY c.depth LIMIT 1) line ON true
WHERE e.level <> 'enterprise';

CREATE OR REPLACE VIEW report.uom AS
SELECT u.id AS uom_id, u.code, u.name, u.dimension
FROM core.uom u;

CREATE OR REPLACE VIEW report.material AS
SELECT m.id AS material_id, m.code, m.name, m.material_type, c.code AS class_code, c.name AS class_name,
       m.base_uom_id, u.code AS base_uom_code, m.is_lot_tracked, m.shelf_life_days, m.origin, m.external_system, m.external_key, m.is_active
FROM core.material m
JOIN core.uom u ON u.id = m.base_uom_id
LEFT JOIN core.material_class c ON c.id = m.material_class_id;

-- Reasons of every kind, with the groups above them (D-062) as a path: "Mechanical › Die".
CREATE OR REPLACE VIEW report.reason AS
WITH RECURSIVE tree (id, depth, group_path) AS (
    SELECT r.id, 0, NULL::text FROM core.reason_code r WHERE r.parent_id IS NULL
    UNION ALL
    SELECT c.id, t.depth + 1, coalesce(t.group_path || ' › ', '') || p.name
    FROM tree t JOIN core.reason_code p ON p.id = t.id JOIN core.reason_code c ON c.parent_id = p.id
) CYCLE id SET is_cycle USING visited
SELECT r.id AS reason_id, r.site_id, r.reason_kind AS kind, r.code, r.name, r.utilization_class, r.oee_impact, t.group_path, t.depth,
       NOT r.is_selectable AS is_group, r.is_active
FROM core.reason_code r
LEFT JOIN tree t ON t.id = r.id AND NOT t.is_cycle;

-- ---- Production and OEE ----

-- One row per production report (D-063), net of any reversal: a reversed report nets to zero (is_reversed), and its correction is a new
-- report. Good = processed - defective. Machine counts aren't reports and aren't here.
CREATE OR REPLACE VIEW report.production_report AS
SELECT c.report_id, max(c.job_id::text)::uuid AS job_id, max(c.run_id::text)::uuid AS run_id, max(c.equipment_id::text)::uuid AS equipment_id,
       min(c.occurred_at) FILTER (WHERE c.source = 'operator') AS occurred_at,
       (min(c.occurred_at) FILTER (WHERE c.source = 'operator') AT TIME ZONE max(s.time_zone))::date AS site_local_date,
       min(c.recorded_at) FILTER (WHERE c.source = 'operator') AS recorded_at,
       coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'processed'), 0)
         - coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'defective'), 0) AS good_qty,
       coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'defective'), 0) AS scrap_qty,
       coalesce(max(c.uom_id::text)::uuid, max(j.uom_id::text)::uuid) AS uom_id,
       max(c.scrap_reason_code_id::text) FILTER (WHERE c.source = 'operator')::uuid AS scrap_reason_id,
       bool_or(c.source = 'correction') AS is_reversed,
       max(c.machine_through) AS machine_through,
       max(c.actor_id) FILTER (WHERE c.source = 'operator') AS reported_by
FROM ts.production_count c
LEFT JOIN core.job j ON j.id = c.job_id
LEFT JOIN core.equipment e ON e.id = c.equipment_id
LEFT JOIN core.equipment s ON s.id = e.site_id
WHERE c.source IN ('operator', 'correction') AND c.report_id IS NOT NULL
GROUP BY c.report_id;

-- Every counted change to good and scrap, in the run it belongs to: reports, their reversals, and lots rejected on quality (D-085), which
-- move their quantity from good to scrap without a report. Adds up to the run's good and scrap; use it for totals. Machine counts excluded.
CREATE OR REPLACE VIEW report.production AS
SELECT c.id AS production_id, c.occurred_at, (c.occurred_at AT TIME ZONE s.time_zone)::date AS site_local_date, c.recorded_at,
       c.equipment_id, c.run_id, c.job_id, c.report_id,
       CASE WHEN c.source = 'correction' THEN 'reversal' WHEN c.report_id IS NULL THEN 'rejection' ELSE 'report' END AS change_kind,
       CASE c.count_kind WHEN 'processed' THEN c.delta_qty WHEN 'defective' THEN -c.delta_qty ELSE 0 END AS good_qty,
       CASE c.count_kind WHEN 'defective' THEN c.delta_qty ELSE 0 END AS scrap_qty,
       coalesce(c.uom_id, j.uom_id) AS uom_id, c.scrap_reason_code_id AS scrap_reason_id, c.actor_id
FROM ts.production_count c
LEFT JOIN core.job j ON j.id = c.job_id
LEFT JOIN core.equipment e ON e.id = c.equipment_id
LEFT JOIN core.equipment s ON s.id = e.site_id
WHERE c.source IN ('operator', 'correction') AND c.count_kind IN ('processed', 'defective');

-- One row per run of a job on a line (D-035), with what was reported into it.
CREATE OR REPLACE VIEW report.run AS
SELECT r.id AS run_id, r.job_id, r.equipment_id, r.run_no, r.started_at, r.ended_at, r.status,
       r.end_reason_code_id AS end_reason_id, r.good_qty, r.scrap_qty, r.uom_id,
       extract(epoch FROM (coalesce(r.ended_at, now()) - r.started_at)) / 3600 AS hours
FROM core.job_run r;

-- Stops as they stand now (D-069: replaced pieces left out), with their reason, its class and the PackML state they started in.
CREATE OR REPLACE VIEW report.stop AS
SELECT d.id AS stop_id, d.equipment_id, d.run_id, d.started_at, d.ended_at,
       extract(epoch FROM (coalesce(d.ended_at, now()) - d.started_at)) / 60 AS minutes,
       (d.started_at AT TIME ZONE s.time_zone)::date AS site_local_date,
       d.packml_state, ps.name AS packml_state_name, d.plc_stop_reason, d.effective_reason_code_id AS reason_id,
       rc.utilization_class, d.effective_reason_code_id IS NULL AS is_uncoded, d.is_micro_stop, d.ended_at IS NULL AS is_open,
       d.source, d.coded_by, d.comment
FROM ts.downtime d
JOIN core.equipment e ON e.id = d.equipment_id
LEFT JOIN core.equipment s ON s.id = e.site_id
LEFT JOIN core.packml_state ps ON ps.code = d.packml_state
LEFT JOIN core.reason_code rc ON rc.id = d.effective_reason_code_id
WHERE d.superseded_at IS NULL;

-- OEE per equipment and shift (D-052, D-072), from the daily rollups: final days only (up to two days ago); today's figures are the app's.
-- Hours from the tally; A, P and Q as the app works them out: planned = scheduled - non-production - planned stops; availability =
-- run / planned; performance = earned / run with a rate; quality = earned good / earned; OEE = A x P x Q. Null where a part can't be worked out.
CREATE OR REPLACE VIEW report.oee_shift AS
WITH w AS (
    SELECT o.site_id, o.equipment_id, o.production_date, o.rolled_at, x.win
    FROM ts.oee_rollup o, jsonb_array_elements(o.windows) AS x(win)
), t AS (
    SELECT site_id, equipment_id, production_date, rolled_at,
           win->>'shiftCode' AS shift_code, win->>'shiftName' AS shift_name, win->>'crew' AS crew,
           (win->>'start')::timestamptz AS window_start, (win->>'end')::timestamptz AS window_end,
           (win->'tally'->>'scheduled')::numeric / 36e9 AS scheduled_hours,
           (win->'tally'->>'nonProduction')::numeric / 36e9 AS non_production_hours,
           (win->'tally'->>'plannedStop')::numeric / 36e9 AS planned_stop_hours,
           (win->'tally'->>'run')::numeric / 36e9 AS run_hours,
           (win->'tally'->>'down')::numeric / 36e9 AS down_hours,
           (win->'tally'->>'microStop')::numeric / 36e9 AS micro_stop_hours,
           (win->'tally'->>'noSignal')::numeric / 36e9 AS no_signal_hours,
           (win->'tally'->>'uncoded')::numeric / 36e9 AS uncoded_hours,
           (win->'tally'->>'runWithRate')::numeric / 36e9 AS run_with_rate_hours,
           (win->'tally'->>'runWithoutJob')::numeric / 36e9 AS run_without_job_hours,
           (win->'tally'->>'stops')::int AS stops,
           (win->'tally'->>'microStops')::int AS micro_stops,
           (win->'tally'->>'good')::numeric AS good_qty,
           (win->'tally'->>'scrap')::numeric AS scrap_qty,
           (win->'tally'->>'earnedHours')::numeric AS earned_hours,
           (win->'tally'->>'earnedGoodHours')::numeric AS earned_good_hours
    FROM w
)
SELECT t.*,
       t.scheduled_hours - t.non_production_hours - t.planned_stop_hours AS planned_hours,
       t.run_hours / nullif(t.scheduled_hours - t.non_production_hours - t.planned_stop_hours, 0) AS availability,
       t.earned_hours / nullif(t.run_with_rate_hours, 0) AS performance,
       t.earned_good_hours / nullif(t.earned_hours, 0) AS quality,
       (t.run_hours / nullif(t.scheduled_hours - t.non_production_hours - t.planned_stop_hours, 0))
         * (t.earned_hours / nullif(t.run_with_rate_hours, 0))
         * (t.earned_good_hours / nullif(t.earned_hours, 0)) AS oee
FROM t;

-- OEE per equipment and production day: its shifts' hours added up, then A, P and Q worked out from the sums (never an average of ratios).
CREATE OR REPLACE VIEW report.oee_day AS
WITH d AS (
    SELECT site_id, equipment_id, production_date,
           sum(scheduled_hours) AS scheduled_hours, sum(non_production_hours) AS non_production_hours, sum(planned_stop_hours) AS planned_stop_hours,
           sum(run_hours) AS run_hours, sum(down_hours) AS down_hours, sum(micro_stop_hours) AS micro_stop_hours,
           sum(no_signal_hours) AS no_signal_hours, sum(uncoded_hours) AS uncoded_hours, sum(run_with_rate_hours) AS run_with_rate_hours,
           sum(stops) AS stops, sum(micro_stops) AS micro_stops, sum(good_qty) AS good_qty, sum(scrap_qty) AS scrap_qty,
           sum(earned_hours) AS earned_hours, sum(earned_good_hours) AS earned_good_hours
    FROM report.oee_shift
    GROUP BY site_id, equipment_id, production_date
)
SELECT d.*,
       d.scheduled_hours - d.non_production_hours - d.planned_stop_hours AS planned_hours,
       d.run_hours / nullif(d.scheduled_hours - d.non_production_hours - d.planned_stop_hours, 0) AS availability,
       d.earned_hours / nullif(d.run_with_rate_hours, 0) AS performance,
       d.earned_good_hours / nullif(d.earned_hours, 0) AS quality,
       (d.run_hours / nullif(d.scheduled_hours - d.non_production_hours - d.planned_stop_hours, 0))
         * (d.earned_hours / nullif(d.run_with_rate_hours, 0))
         * (d.earned_good_hours / nullif(d.earned_hours, 0)) AS oee
FROM d;

-- ---- Orders and jobs ----

-- Work orders (D-003) with what their production jobs reported, in the order's unit when the jobs share it.
CREATE OR REPLACE VIEW report.work_order AS
SELECT w.id AS work_order_id, w.site_id, w.order_number, w.material_id, w.planned_qty, w.uom_id, w.status, w.priority,
       w.planned_start, w.planned_end, w.due_at, w.origin, w.external_system, w.external_key, w.created_at,
       coalesce(sum(r.good_qty) FILTER (WHERE j.uom_id = w.uom_id), 0) AS good_qty,
       coalesce(sum(r.scrap_qty) FILTER (WHERE j.uom_id = w.uom_id), 0) AS scrap_qty,
       min(r.started_at) AS first_started_at, max(r.ended_at) AS last_ended_at,
       count(DISTINCT j.id) AS job_count
FROM core.work_order w
LEFT JOIN core.job j ON j.work_order_id = w.id AND j.job_type = 'production'
LEFT JOIN core.job_run r ON r.job_id = j.id
GROUP BY w.id;

-- Jobs (D-003, D-018) with their line (or the group they were planned to), plan, runs and what was reported.
CREATE OR REPLACE VIEW report.job AS
SELECT j.id AS job_id, j.site_id, j.work_order_id, j.job_number, j.job_type, j.equipment_id, j.group_equipment_id, j.material_id,
       o.operation_number, j.planned_qty, j.uom_id, j.status, j.planned_start, j.planned_end, j.completed_at, j.created_at,
       coalesce(sum(r.good_qty), 0) AS good_qty, coalesce(sum(r.scrap_qty), 0) AS scrap_qty,
       min(r.started_at) AS first_started_at, max(r.ended_at) AS last_ended_at, count(r.id) AS run_count,
       coalesce(sum(extract(epoch FROM (coalesce(r.ended_at, now()) - r.started_at)) / 3600), 0) AS run_hours
FROM core.job j
LEFT JOIN core.process_operation o ON o.id = j.process_operation_id
LEFT JOIN core.job_run r ON r.job_id = j.id
GROUP BY j.id, o.operation_number;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_report') THEN
        GRANT USAGE ON SCHEMA report TO pulaski_report;
        GRANT SELECT ON ALL TABLES IN SCHEMA report TO pulaski_report;
    END IF;
END $$;
