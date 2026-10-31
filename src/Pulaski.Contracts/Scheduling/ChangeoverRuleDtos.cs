namespace Pulaski.Contracts.Scheduling;

// ---- Changeover rules (D-015, D-074) ----

/// <summary>
/// A rule of a site's changeover matrix: the time a line needs to change from one material (or class, or anything) to another. The most
/// specific rule that matches wins; with none, the line's standard changeover for the incoming material.
/// </summary>
public sealed record ChangeoverRuleDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }

    /// <summary>The line; null for every line of the site.</summary>
    public Guid? EquipmentId { get; init; }
    public string? EquipmentCode { get; init; }
    public Guid? FromMaterialId { get; init; }
    public string? FromMaterialCode { get; init; }
    public string? FromMaterialName { get; init; }
    public Guid? FromMaterialClassId { get; init; }
    public string? FromMaterialClassCode { get; init; }
    public Guid? ToMaterialId { get; init; }
    public string? ToMaterialCode { get; init; }
    public string? ToMaterialName { get; init; }
    public Guid? ToMaterialClassId { get; init; }
    public string? ToMaterialClassCode { get; init; }
    public int DurationMinutes { get; init; }
    public string? Note { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

/// <summary>A new rule. Each side names a material or a class, or neither for anything; the line is fixed once made.</summary>
public sealed record CreateChangeoverRuleRequest
{
    public Guid SiteId { get; init; }
    public Guid? EquipmentId { get; init; }
    public Guid? FromMaterialId { get; init; }
    public Guid? FromMaterialClassId { get; init; }
    public Guid? ToMaterialId { get; init; }
    public Guid? ToMaterialClassId { get; init; }
    public int DurationMinutes { get; init; }
    public string? Note { get; init; }
}

/// <summary>A rule's time, note, or whether it is on; what it matches is fixed: switch it off and add another.</summary>
public sealed record UpdateChangeoverRuleRequest
{
    public long Version { get; init; }
    public int DurationMinutes { get; init; }
    public string? Note { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>The columns of a changeover rules CSV file (D-074), as the screen offers them; tested against the parser's.</summary>
public static class ChangeoverRuleCsvFormat
{
    public const string Header = "line,from_material,from_class,to_material,to_class,minutes,note,active";
}
