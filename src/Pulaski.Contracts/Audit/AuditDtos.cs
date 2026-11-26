namespace Pulaski.Contracts.Audit;

/// <summary>A page of the audit trail (#129, D-128), newest first; <see cref="NextBefore"/> asks for the page after it.</summary>
public sealed record AuditPageDto
{
    public IReadOnlyList<AuditEventDto> Events { get; init; } = [];

    /// <summary>The cursor for older events (<c>recorded_at|id</c>), null on the last page.</summary>
    public string? NextBefore { get; init; }
}

/// <summary>One recorded change: what, to what, by whom, when, and what changed.</summary>
public sealed record AuditEventDto
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }

    /// <summary>"capability changed" from <c>pulaski.capability.changed.v1</c>.</summary>
    public required string Action { get; init; }
    public required string SubjectType { get; init; }
    public required Guid Subject { get; init; }

    /// <summary>A code, number or name the change names its subject by, when it has one.</summary>
    public string? SubjectLabel { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public required string ActorType { get; init; }
    public required string ActorId { get; init; }
    public string? ActorName { get; init; }
    public string Source { get; init; } = "";
    public string? Reason { get; init; }

    /// <summary>For a change that carries before and after: only the fields that differ.</summary>
    public IReadOnlyList<AuditFieldChangeDto> Changes { get; init; } = [];

    /// <summary>Otherwise: the event's data as fields.</summary>
    public IReadOnlyList<AuditFieldDto> Fields { get; init; } = [];
    public required string DataJson { get; init; }
}

public sealed record AuditFieldChangeDto(string Field, string? Before, string? After);

public sealed record AuditFieldDto(string Field, string? Value);

/// <summary>The areas (subject types) changes are recorded under (#129), for choosing one. Kept in step with the code by a test.</summary>
public static class AuditAreas
{
    public static IReadOnlyList<string> All { get; } =
    [
        "andon_call", "andon_type", "app_user", "bom", "calendar_exception", "changeover_rule", "container", "control_limit", "crew", "delivery",
        "delivery_feed", "edge_node", "equipment", "equipment_capability", "expected_receipt", "feed_shortfall", "handover_entry", "job",
        "label_printer", "labor_approval", "labor_break", "labor_session", "language", "lot", "lot_attribute", "lot_document", "machine_profile",
        "maintenance_plan", "maintenance_request", "material", "material_class", "material_connection", "material_spec", "nonconformance",
        "partial_batch", "person", "process", "qualification", "quality_characteristic", "quality_result", "reason_code", "role", "run_standard",
        "shift_schedule", "site", "spc_alert", "stock_count", "supplier_barcode_format", "terminal", "uom", "work_instruction", "work_order",
    ];
}
