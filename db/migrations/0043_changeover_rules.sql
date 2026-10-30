-- 0043 · Changeover rules screen (D-074, #69): the permission to maintain them, granted to the planner and process engineer roles that
-- came with the database. The table (D-015) is unchanged. Re-runnable; no BEGIN/COMMIT.

INSERT INTO sec.permission (code, description) VALUES
    ('changeovers.manage', 'Maintain changeover rules: how long each change of material takes on a line')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'changeovers.manage' FROM sec.role r WHERE r.code IN ('planner', 'process_engineer') AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- Descriptions change only where an administrator hasn't rewritten them.
UPDATE sec.role SET description = 'Plans and sequences jobs, manages work orders, planning rates, changeover times and materials. Sees the floor, inventory and processes.'
WHERE code = 'planner' AND description = 'Plans and sequences jobs, manages work orders, planning rates and materials. Sees the floor, inventory and processes.';
UPDATE sec.role SET description = 'Maintains processes, BOMs, capabilities and changeover times. Sees equipment, materials, units and the schedule.'
WHERE code = 'process_engineer' AND description = 'Maintains processes, BOMs and capabilities. Sees equipment, materials and units.';
