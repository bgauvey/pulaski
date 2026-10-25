namespace Pulaski.Infrastructure.Materials;

/// <summary>SQL shared by the material lists, the material page and the repository (D-033).</summary>
internal static class MaterialSql
{
    public const string Select = """
        SELECT m.id, m.code, m.name, m.description, m.material_type, m.base_uom_id, u.code AS base_uom_code,
               m.material_class_id, c.code AS material_class_code, c.name AS material_class_name,
               m.is_lot_tracked, m.shelf_life_days, m.is_active, m.batch_size, m.batch_tolerance_pct, m.batch_rule, m.erp_batch_rule,
               m.origin, m.external_system, m.external_key, m.external_version, m.updated_at, m.xmin::text::bigint AS version
        FROM core.material m
        JOIN core.uom u ON u.id = m.base_uom_id
        LEFT JOIN core.material_class c ON c.id = m.material_class_id
        """;

    private const string OnHand = "l.quantity > 0 AND l.status IN ('available','on_hold','quarantine')";

    // Anything that refers to the material fixes its base unit and its local code (D-033).
    public const string Use = $"""
        SELECT (EXISTS (SELECT 1 FROM core.lot WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.bom WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.bom_item WHERE component_material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.bom_version v CROSS JOIN LATERAL jsonb_array_elements(v.definition->'items') it
                        WHERE v.status = 'draft' AND it->>'componentMaterialId' = @Id::text)
             OR EXISTS (SELECT 1 FROM core.manufacturing_order WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.work_order WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.job WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.job_material WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.equipment_capability WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.run_standard WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.process_material WHERE material_id = @Id)
             OR EXISTS (SELECT 1 FROM core.changeover_rule WHERE from_material_id = @Id OR to_material_id = @Id)) AS in_use,
               EXISTS (SELECT 1 FROM core.lot l WHERE l.material_id = @Id AND {OnHand}) AS has_stock_on_hand
        """;

    public const string Usage = $"""
        SELECT (SELECT count(*)::int FROM core.lot l WHERE l.material_id = @Id AND {OnHand}) AS lots_on_hand,
               (SELECT coalesce(sum(l.quantity), 0) FROM core.lot l WHERE l.material_id = @Id AND {OnHand}) AS quantity_on_hand,
               (SELECT count(*)::int FROM core.job j
                WHERE j.status NOT IN ('completed','closed','cancelled')
                  AND (j.material_id = @Id OR EXISTS (SELECT 1 FROM core.job_material jm WHERE jm.job_id = j.id AND jm.material_id = @Id))) AS open_jobs,
               (SELECT count(*)::int FROM core.work_order w
                WHERE w.material_id = @Id AND w.status NOT IN ('completed','closed','cancelled')) AS open_work_orders,
               (SELECT count(*)::int FROM core.bom b WHERE b.material_id = @Id) AS boms,
               (SELECT count(DISTINCT v.id)::int FROM core.bom_item i JOIN core.bom_version v ON v.id = i.bom_version_id AND v.status = 'released'
                WHERE i.component_material_id = @Id) AS used_in_released_boms,
               (SELECT count(*)::int FROM core.equipment_capability c WHERE c.material_id = @Id) AS capabilities,
               (SELECT count(*)::int FROM core.process_material p WHERE p.material_id = @Id AND p.is_active) AS processes
        """;
}
