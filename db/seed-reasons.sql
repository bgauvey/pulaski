-- =====================================================================
-- Pulaski — starter reason codes                            2026-10-16
-- Enterprise-wide reasons for inventory adjustments and holds (D-027), scrap and run ends (D-035), and
-- utilization reasons for machine time (D-041).
-- Load after schema-draft.sql. Re-runnable: existing codes are left alone.
--
-- Like the rest of the seed these are master data loaded before go-live,
-- so they carry no creation events.
-- =====================================================================

INSERT INTO core.reason_code (site_id, reason_kind, code, name, requires_comment, sort_order) VALUES
    -- Write up and write off.
    (NULL, 'inventory_adjustment', 'COUNT',     'Count correction',            false, 10),
    (NULL, 'inventory_adjustment', 'FOUND',     'Found stock',                 false, 20),
    (NULL, 'inventory_adjustment', 'DAMAGE',    'Damaged',                     false, 30),
    (NULL, 'inventory_adjustment', 'CONTAM',    'Contaminated',                false, 40),
    (NULL, 'inventory_adjustment', 'EXPIRED',   'Expired',                     false, 50),
    (NULL, 'inventory_adjustment', 'SPILL',     'Spilled or lost in handling', false, 60),
    (NULL, 'inventory_adjustment', 'ERP-SYNC',  'Agreed with the ERP',         true,  70),
    (NULL, 'inventory_adjustment', 'OTHER',     'Other',                       true,  90),
    -- Hold and quarantine.
    (NULL, 'hold', 'QA-HOLD',   'Awaiting quality release', false, 10),
    (NULL, 'hold', 'SUSPECT',   'Suspect quality',          true,  20),
    (NULL, 'hold', 'SUPPLIER',  'Supplier notice',          true,  30),
    (NULL, 'hold', 'CUSTOMER',  'Customer complaint',       true,  40),
    (NULL, 'hold', 'PARTIAL',   'Partial batch awaiting approval', false, 50),
    (NULL, 'hold', 'OTHER',     'Other',                    true,  90),
    -- Scrap reported against a run (D-035).
    (NULL, 'scrap', 'STARTUP',   'Start-up scrap',           false, 10),
    (NULL, 'scrap', 'DIMENSION', 'Out of dimension',         false, 20),
    (NULL, 'scrap', 'SURFACE',   'Surface defect',           false, 30),
    (NULL, 'scrap', 'COLOUR',    'Colour off',               false, 40),
    (NULL, 'scrap', 'CHANGEOVER','Changeover scrap',         false, 50),
    (NULL, 'scrap', 'OTHER',     'Other',                    true,  90),
    -- Why a run ended without completing its job (D-035).
    (NULL, 'run_end', 'SHIFT',     'End of shift',           false, 10),
    (NULL, 'run_end', 'MATERIAL',  'Waiting for material',   false, 20),
    (NULL, 'run_end', 'BREAKDOWN', 'Breakdown',              false, 30),
    (NULL, 'run_end', 'PRIORITY',  'Higher-priority job',    false, 40),
    (NULL, 'run_end', 'QUALITY',   'Quality problem',        false, 50),
    (NULL, 'run_end', 'OTHER',     'Other',                  true,  90)
ON CONFLICT (site_id, reason_kind, code) DO NOTHING;

-- Utilization reasons (D-041): what a machine's time counts as. Runtime is run time; downtime is availability
-- loss; planned is scheduled non-production, left out of planned time. Machine profiles map stop codes to these.
INSERT INTO core.reason_code (site_id, reason_kind, utilization_class, code, name, requires_comment, sort_order) VALUES
    (NULL, 'utilization', 'runtime',  'RUN',       'Running',                          false, 10),
    (NULL, 'utilization', 'runtime',  'SLOW',      'Running below rate',               false, 20),
    (NULL, 'utilization', 'downtime', 'JAM',       'Jam',                              false, 110),
    (NULL, 'utilization', 'downtime', 'FAULT',     'Machine fault',                    false, 120),
    (NULL, 'utilization', 'downtime', 'ADJUST',    'Adjustment',                       false, 130),
    (NULL, 'utilization', 'downtime', 'STARVED',   'Starved: waiting for material or upstream', false, 140),
    (NULL, 'utilization', 'downtime', 'BLOCKED',   'Blocked: downstream full',         false, 150),
    (NULL, 'utilization', 'downtime', 'QUALITY',   'Quality stop',                     false, 160),
    (NULL, 'utilization', 'downtime', 'NO-CREW',   'No operator',                      false, 170),
    (NULL, 'utilization', 'downtime', 'NO-ORDER',  'Available, nothing scheduled',     false, 180),
    (NULL, 'utilization', 'downtime', 'OTHER',     'Other',                            true,  190),
    (NULL, 'utilization', 'planned',  'SETUP',     'Setup and changeover',             false, 210),
    (NULL, 'utilization', 'planned',  'TEARDOWN',  'Teardown',                         false, 220),
    (NULL, 'utilization', 'planned',  'CLEAN',     'Planned cleaning',                 false, 230),
    (NULL, 'utilization', 'planned',  'PM',        'Planned maintenance',              false, 240),
    (NULL, 'utilization', 'planned',  'BREAK',     'Scheduled break',                  false, 250)
ON CONFLICT (site_id, reason_kind, code) DO NOTHING;
