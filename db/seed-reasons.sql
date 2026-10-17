-- =====================================================================
-- Pulaski — starter reason codes                            2026-10-16
-- Enterprise-wide reasons for inventory adjustments and holds (D-027).
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
    (NULL, 'hold', 'OTHER',     'Other',                    true,  90)
ON CONFLICT (site_id, reason_kind, code) DO NOTHING;
