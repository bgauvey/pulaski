-- 0013 · View permissions (D-039): a user sees only the screens, and data, their roles let them see. Adds the view
-- permissions and gives the starting roles theirs (an administrator may change any role afterwards; roles that already
-- exist keep what they have and gain only these). Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

INSERT INTO sec.permission (code, description) VALUES
    ('overview.view', 'See the Overview'),
    ('work_orders.view', 'See work orders and their progress'),
    ('schedule.view', 'See the schedule'),
    ('floor.view', 'See the floor and job pages: runs, reports and partial batches'),
    ('inventory.view', 'See inventory: stock, lots, movements, counts, shortages and reports'),
    ('equipment.view', 'See the equipment hierarchy and its settings'),
    ('materials.view', 'See materials, their classes, capabilities and rates'),
    ('processes.view', 'See processes and BOMs'),
    ('units.view', 'See units and material conversions'),
    ('reasons.view', 'See reason codes'),
    ('shifts.view', 'See shift schedules')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, v.permission_code FROM (VALUES
    ('planner', 'overview.view'),
    ('planner', 'work_orders.view'),
    ('planner', 'schedule.view'),
    ('planner', 'floor.view'),
    ('planner', 'inventory.view'),
    ('planner', 'materials.view'),
    ('planner', 'processes.view'),
    ('supervisor', 'overview.view'),
    ('supervisor', 'floor.view'),
    ('supervisor', 'inventory.view'),
    ('supervisor', 'work_orders.view'),
    ('operator', 'overview.view'),
    ('operator', 'floor.view'),
    ('operator', 'inventory.view'),
    ('operator', 'work_orders.view'),
    ('quality', 'overview.view'),
    ('quality', 'floor.view'),
    ('quality', 'inventory.view'),
    ('quality', 'materials.view'),
    ('manager', 'overview.view'),
    ('manager', 'work_orders.view'),
    ('manager', 'schedule.view'),
    ('manager', 'floor.view'),
    ('manager', 'inventory.view'),
    ('manager', 'equipment.view'),
    ('manager', 'materials.view'),
    ('manager', 'processes.view'),
    ('manager', 'units.view'),
    ('manager', 'reasons.view'),
    ('manager', 'shifts.view'),
    ('auditor', 'overview.view'),
    ('auditor', 'work_orders.view'),
    ('auditor', 'schedule.view'),
    ('auditor', 'floor.view'),
    ('auditor', 'inventory.view'),
    ('auditor', 'equipment.view'),
    ('auditor', 'materials.view'),
    ('auditor', 'processes.view'),
    ('auditor', 'units.view'),
    ('auditor', 'reasons.view'),
    ('auditor', 'shifts.view'),
    ('process_engineer', 'equipment.view'),
    ('process_engineer', 'materials.view'),
    ('process_engineer', 'processes.view'),
    ('process_engineer', 'units.view')
) AS v(role_code, permission_code)
JOIN sec.role r ON r.code = v.role_code AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- The starting roles' descriptions say what they now see, where nobody has rewritten them.
UPDATE sec.role SET description = v.description FROM (VALUES
    ('planner', 'Plans and sequences jobs, manages work orders, planning rates and materials. Sees the floor, inventory and processes.'),
    ('process_engineer', 'Maintains processes, BOMs and capabilities. Sees equipment, materials and units.')
) AS v(code, description)
WHERE sec.role.code = v.code AND sec.role.is_system
  AND sec.role.description IN ('Plans and sequences jobs, manages work orders, planning rates and materials. Views the floor and inventory.',
                               'Maintains processes, BOMs and capabilities.');
