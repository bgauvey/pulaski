-- =====================================================================
-- Pulaski — demo seed data                                  2026-10-12
-- Load after schema-draft.sql, into an empty database. One enterprise,
-- one site and one simulated extrusion line, matching the first
-- vertical slice in docs/charter.md.
--
-- Seed rows are master data loaded before go-live, so they carry no
-- creation events; everything the application changes afterwards does.
-- uuidv7() is used here only because this is a manual script (D-006).
-- =====================================================================

-- Partitions for the current month and the next two.
SELECT ts.create_month_partitions('evt.event', current_date, 3);

INSERT INTO core.uom (code, name, dimension, to_base_factor) VALUES
    ('kg', 'Kilogram', 'mass',   1),
    ('lb', 'Pound',    'mass',   0.45359237),
    ('m',  'Metre',    'length', 1),
    ('ft', 'Foot',     'length', 0.3048),
    ('ea', 'Each',     'count',  1);

INSERT INTO core.material_class (code, name) VALUES
    ('RESIN',   'Resins'),
    ('PROFILE', 'Extruded profiles');

INSERT INTO core.material (code, name, material_type, material_class_id, base_uom_id)
SELECT v.code, v.name, v.material_type, mc.id, u.id
FROM (VALUES
    ('PVC-K67',     'PVC compound K67, white',      'raw',      'RESIN',   'kg'),
    ('TIO2-MB',     'TiO2 masterbatch',             'raw',      'RESIN',   'kg'),
    ('PRF-4410-WH', 'Window frame profile 4410, white', 'finished', 'PROFILE', 'm')
) AS v(code, name, material_type, class_code, uom_code)
JOIN core.material_class mc ON mc.code = v.class_code
JOIN core.uom u ON u.code = v.uom_code;

WITH ids AS (
    SELECT uuidv7() AS ent, uuidv7() AS site, uuidv7() AS area, uuidv7() AS line
)
INSERT INTO core.equipment (id, parent_id, site_id, code, name, description, level, equipment_role,
                            tracks_state, is_schedulable, holds_inventory, time_zone)
SELECT v.* FROM ids, LATERAL (VALUES
    (ent,       NULL::uuid, NULL::uuid, 'DEMO',       'Demo Manufacturing', NULL,                     'enterprise',       'enterprise',      false, false, false, NULL),
    (site,      ent,  site, 'PLT1',       'Plant 1',            'Demo extrusion plant',   'site',             'site',            false, false, false, 'America/Chicago'),
    (area,      site, site, 'EXT',        'Extrusion',          NULL,                     'area',             'area',            false, false, false, NULL),
    (line,      area, site, 'EXT-01',     'Extrusion line 1',   'Profile extrusion line', 'work_center',      'production_line', true,  true,  false, NULL),
    (uuidv7(),  line, site, 'EXT-01-HOP', 'Hopper',             'Resin feed point',       'equipment_module', 'feed_point',      false, false, true,  NULL),
    (uuidv7(),  line, site, 'EXT-01-EXT', 'Extruder',           'Twin-screw extruder',    'work_unit',        'machine',         true,  false, false, NULL),
    (uuidv7(),  line, site, 'EXT-01-PUL', 'Puller',             NULL,                     'work_unit',        'machine',         true,  false, false, NULL),
    (uuidv7(),  line, site, 'EXT-01-SAW', 'Saw',                NULL,                     'work_unit',        'machine',         true,  false, false, NULL)
) AS v;

-- Closure table: every ancestor/descendant pair, including self at depth 0.
INSERT INTO core.equipment_closure (ancestor_id, descendant_id, depth)
WITH RECURSIVE pairs AS (
    SELECT e.id AS ancestor_id, e.id AS descendant_id, 0 AS depth
    FROM core.equipment e
    UNION ALL
    SELECT p.ancestor_id, c.id, p.depth + 1
    FROM pairs p
    JOIN core.equipment c ON c.parent_id = p.descendant_id
)
SELECT ancestor_id, descendant_id, depth FROM pairs;
