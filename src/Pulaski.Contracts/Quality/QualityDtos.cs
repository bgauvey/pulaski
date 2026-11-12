namespace Pulaski.Contracts.Quality;

/// <summary>Something measured or inspected (#65): <c>variable</c> takes values, <c>attribute</c> inspected and defective counts.</summary>
public sealed record CharacteristicDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string DataKind { get; init; }
    public Guid? UomId { get; init; }
    public string? UomCode { get; init; }
    public int Decimals { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }

    /// <summary>Its control chart (D-099): <c>i_mr</c>, <c>xbar_r</c>, <c>xbar_s</c>, <c>p</c>, <c>np</c>, <c>c</c> or <c>u</c>.</summary>
    public string ChartType { get; init; } = "i_mr";

    /// <summary>Values a subgroup holds (1 for individuals, 2–25 for X-bar charts), or items inspected a sample for np and c charts.</summary>
    public int SubgroupSize { get; init; } = 1;

    /// <summary>Control limits are in force or still to come, so the chart can't change.</summary>
    public bool HasLimits { get; init; }
}

public sealed record CreateCharacteristicRequest
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string DataKind { get; init; } = "variable";
    public Guid? UomId { get; init; }
    public int Decimals { get; init; } = 3;
}

/// <summary>Code and kind are fixed once created.</summary>
public sealed record UpdateCharacteristicRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public Guid? UomId { get; init; }
    public int Decimals { get; init; } = 3;
    public bool IsActive { get; init; } = true;

    /// <summary>A new control chart (D-099); left out, it stays. Refused while control limits are in force.</summary>
    public string? ChartType { get; init; }
    public int? SubgroupSize { get; init; }
}

/// <summary>A set of control limits over a period (D-099).</summary>
public sealed record ControlLimitDto
{
    public Guid Id { get; init; }
    public Guid CharacteristicId { get; init; }
    public Guid MaterialId { get; init; }
    public string MaterialCode { get; init; } = "";
    public string MaterialName { get; init; } = "";

    /// <summary>The line whose own limits these are; null for every line.</summary>
    public Guid? EquipmentId { get; init; }
    public string? EquipmentCode { get; init; }
    public string ChartType { get; init; } = "";
    public int SubgroupSize { get; init; }
    public double CenterLine { get; init; }
    public double Ucl { get; init; }
    public double Lcl { get; init; }
    public double? DispersionCenter { get; init; }
    public double? DispersionUcl { get; init; }
    public double? DispersionLcl { get; init; }

    /// <summary><c>computed</c> from a baseline, or <c>manual</c>.</summary>
    public string Method { get; init; } = "";
    public DateTimeOffset? BaselineFrom { get; init; }
    public DateTimeOffset? BaselineTo { get; init; }
    public int? BaselineSubgroups { get; init; }
    public IReadOnlyList<Guid> LeftOutSampleIds { get; init; } = [];
    public string? Note { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public string ApprovedBy { get; init; } = "";
    public DateTimeOffset ApprovedAt { get; init; }
    public string? EndedBy { get; init; }
    public bool IsInEffect { get; init; }
}

/// <summary>Limits worked out from a baseline (D-099), for review before approval; nothing is saved.</summary>
public sealed record LimitBaselineDto
{
    public Guid CharacteristicId { get; init; }
    public string ChartType { get; init; } = "";
    public int SubgroupSize { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public int Subgroups { get; init; }
    public int MinSubgroups { get; init; }

    /// <summary>The limits the baseline gives; all null with <see cref="NotEnough"/> saying why.</summary>
    public double? CenterLine { get; init; }
    public double? Ucl { get; init; }
    public double? Lcl { get; init; }
    public double? DispersionCenter { get; init; }
    public double? DispersionUcl { get; init; }
    public double? DispersionLcl { get; init; }
    public string? NotEnough { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Every result in the window, in time order, with its plotted value and whether it was used.</summary>
    public IReadOnlyList<BaselinePointDto> Points { get; init; } = [];

    /// <summary>The limits in force now for the same characteristic, material and line, if any, to compare.</summary>
    public ControlLimitDto? Current { get; init; }
}

/// <summary>One result of a baseline: its plotted statistic, and why it was left out, if it was.</summary>
public sealed record BaselinePointDto
{
    public Guid SampleId { get; init; }
    public DateTimeOffset At { get; init; }
    public double? Value { get; init; }
    public double? Dispersion { get; init; }
    public int Size { get; init; }

    /// <summary>The reviewer left it out.</summary>
    public bool IsLeftOut { get; init; }

    /// <summary>Why the chart couldn't take it (wrong size, no values), if it couldn't.</summary>
    public string? Unusable { get; init; }

    /// <summary>Beyond the limits worked out (a point to look into before approving).</summary>
    public bool IsBeyondLimits { get; init; }
}

/// <summary>Approves the limits a reviewed baseline gives; worked out again first, and refused (409) when they differ from those reviewed.</summary>
public sealed record ApproveControlLimitsRequest
{
    public Guid MaterialId { get; init; }
    public Guid? EquipmentId { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public IReadOnlyList<Guid> LeftOutSampleIds { get; init; } = [];
    public double CenterLine { get; init; }
    public double Ucl { get; init; }
    public double Lcl { get; init; }

    /// <summary>When they start; now when left out. Never in the past.</summary>
    public DateTimeOffset? EffectiveFrom { get; init; }
    public string? Note { get; init; }
}

/// <summary>Limits entered by hand, with why (D-099).</summary>
public sealed record SetManualControlLimitsRequest
{
    public Guid MaterialId { get; init; }
    public Guid? EquipmentId { get; init; }
    public double CenterLine { get; init; }
    public double Ucl { get; init; }
    public double Lcl { get; init; }
    public double? DispersionCenter { get; init; }
    public double? DispersionUcl { get; init; }
    public double? DispersionLcl { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public string Note { get; init; } = "";
}

public sealed record EndControlLimitsRequest
{
    public string? Note { get; init; }
}

/// <summary>
/// A control chart of a characteristic's results for a material at a site (D-099): each result's plotted value with the limits in force for it at its
/// time (its line's own first, else the material's), oldest first.
/// </summary>
public sealed record ControlChartDto
{
    public Guid CharacteristicId { get; init; }
    public string CharacteristicCode { get; init; } = "";
    public string CharacteristicName { get; init; } = "";
    public string? UomCode { get; init; }
    public int Decimals { get; init; }
    public string ChartType { get; init; } = "";
    public int SubgroupSize { get; init; }
    public Guid MaterialId { get; init; }
    public string MaterialCode { get; init; } = "";
    public Guid? EquipmentId { get; init; }
    public string? EquipmentCode { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public IReadOnlyList<ControlChartPointDto> Points { get; init; } = [];

    /// <summary>Results the chart can't take (a size that doesn't fit, no values), left off.</summary>
    public int Unusable { get; init; }

    /// <summary>More results than a chart shows: only the latest <see cref="MaxPoints"/> are.</summary>
    public bool IsTruncated { get; init; }
    public int MaxPoints { get; init; }

    /// <summary>The periods of limits the points use.</summary>
    public IReadOnlyList<ControlLimitDto> Limits { get; init; } = [];
}

/// <summary>One plotted result, with its limits (null when none were in force) and whether it is beyond them.</summary>
public sealed record ControlChartPointDto
{
    public Guid SampleId { get; init; }
    public DateTimeOffset At { get; init; }
    public double Value { get; init; }

    /// <summary>Range, standard deviation or moving range; null on charts without one (and for the first moving range).</summary>
    public double? Dispersion { get; init; }
    public int Size { get; init; }
    public string? LineCode { get; init; }
    public string? LotLabel { get; init; }
    public string? JobNumber { get; init; }
    public Guid? LimitId { get; init; }
    public double? Center { get; init; }
    public double? Ucl { get; init; }
    public double? Lcl { get; init; }
    public double? DispersionCenter { get; init; }
    public double? DispersionUcl { get; init; }
    public double? DispersionLcl { get; init; }
    public bool IsBeyondLimits { get; init; }
    public bool IsDispersionBeyondLimits { get; init; }
}

/// <summary>The limits saved, and those it ended.</summary>
public sealed record ControlLimitChangeResponse(Guid Id, IReadOnlyList<Guid> Ended);

/// <summary>A material's specification in a list.</summary>
public sealed record SpecSummaryDto
{
    public required Guid Id { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public bool IsActive { get; init; }
    public int? ReleasedVersion { get; init; }
    public int? DraftVersion { get; init; }
    public int ReleasedItems { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SpecDto
{
    public required Guid Id { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<SpecVersionSummaryDto> Versions { get; init; } = [];
}

public sealed record SpecVersionSummaryDto(Guid Id, int Number, string Status, DateTimeOffset? ReleasedAt, string? ReleasedBy, string? Note);

public sealed record SpecVersionDto
{
    public required Guid Id { get; init; }
    public required Guid SpecId { get; init; }
    public int Number { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public string? ReleasedBy { get; init; }
    public string? Note { get; init; }
    public long Version { get; init; }
    public SpecDefinitionDto Definition { get; init; } = new();

    /// <summary>For a draft: what still stops it being released.</summary>
    public IReadOnlyList<SpecProblemDto> Problems { get; init; } = [];
}

public sealed record SpecDefinitionDto
{
    public IReadOnlyList<SpecItemDto> Items { get; init; } = [];
}

/// <summary>
/// One characteristic and its limits: a variable's lower and/or upper limit and optional target; an attribute's most defects allowed in a
/// sample. <see cref="HoldOnFail"/>: a failing result for a lot puts the lot on hold.
/// </summary>
public sealed record SpecItemDto
{
    public Guid Id { get; init; }
    public Guid CharacteristicId { get; init; }
    public decimal? Lsl { get; init; }
    public decimal? Target { get; init; }
    public decimal? Usl { get; init; }
    public int? MaxDefects { get; init; }
    public bool HoldOnFail { get; init; }
    public int SortOrder { get; init; }
}

public sealed record SpecProblemDto(string Code, string Message, Guid? ItemId);

public sealed record CreateSpecRequest
{
    public Guid MaterialId { get; init; }
}

public sealed record SaveSpecDraftRequest
{
    public long Version { get; init; }
    public SpecDefinitionDto Definition { get; init; } = new();
}

public sealed record SaveSpecDraftResponse(long Version, IReadOnlyList<SpecProblemDto> Problems);

public sealed record ReleaseSpecVersionRequest
{
    public long Version { get; init; }
    public string? Note { get; init; }
}

/// <summary>A material's released specification, ready to show or print: each characteristic with its limits in words.</summary>
public sealed record ReleasedSpecDto
{
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public int VersionNumber { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public IReadOnlyList<ReleasedSpecItemDto> Items { get; init; } = [];
}

public sealed record ReleasedSpecItemDto
{
    public required Guid ItemId { get; init; }
    public required Guid CharacteristicId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string DataKind { get; init; }
    public string? UomCode { get; init; }
    public decimal? Lsl { get; init; }
    public decimal? Target { get; init; }
    public decimal? Usl { get; init; }
    public int? MaxDefects { get; init; }
    public bool HoldOnFail { get; init; }

    /// <summary>"3.2 – 3.4 (target 3.3)", "at most 1 defect".</summary>
    public required string Limits { get; init; }
}

/// <summary>
/// A result to record against a lot, a job (its run at the time) or both, at the site. Give <see cref="Values"/> for a measured
/// characteristic, or <see cref="Inspected"/> and <see cref="Defects"/> for an inspected one. <see cref="Source"/> is <c>manual</c> or <c>lab</c>.
/// </summary>
public sealed record RecordQualityResultRequest
{
    public Guid CharacteristicId { get; init; }
    public Guid? LotId { get; init; }
    public Guid? JobId { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public IReadOnlyList<decimal>? Values { get; init; }
    public int? Inspected { get; init; }
    public int? Defects { get; init; }
    public string Source { get; init; } = "manual";
    public string? Comment { get; init; }
}

/// <summary>What recording a result did: whether it conformed (null: no limits in force), and whether its lot was put on hold.</summary>
public sealed record QualityResultResponse
{
    public required Guid Id { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public bool? Conforms { get; init; }
    public string? Limits { get; init; }
    public bool LotHeld { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record QualityResultDto
{
    public required Guid Id { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid SiteId { get; init; }
    public required Guid CharacteristicId { get; init; }
    public required string CharacteristicCode { get; init; }
    public required string CharacteristicName { get; init; }
    public required string DataKind { get; init; }
    public string? UomCode { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public Guid? LotId { get; init; }
    public string? LotLabel { get; init; }
    public string? LotStatus { get; init; }
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }
    public string? EquipmentCode { get; init; }
    public IReadOnlyList<decimal> Values { get; init; } = [];
    public decimal? Mean { get; init; }
    public int? Inspected { get; init; }
    public int? Defects { get; init; }

    /// <summary>The limits it was checked against, in words; null when none were in force.</summary>
    public string? Limits { get; init; }
    public int? SpecVersionNumber { get; init; }
    public bool? Conforms { get; init; }
    public required string Source { get; init; }
    public required string RecordedBy { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string? Comment { get; init; }
    public bool IsExcluded { get; init; }
    public string? ExclusionReason { get; init; }
    public string? ExcludedNote { get; init; }
    public string? ExcludedBy { get; init; }
    public DateTimeOffset? ExcludedAt { get; init; }
}

/// <summary>Leaves a wrong result out, with a reason (kind <c>sample_exclusion</c>); it stays on record. <see cref="OccurredAt"/> finds it.</summary>
public sealed record ExcludeQualityResultRequest
{
    public DateTimeOffset OccurredAt { get; init; }
    public Guid ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Rejects a held lot on quality: what is left of it is written off as scrap, and taken off the good output of the run that made it.</summary>
public sealed record RejectLotRequest
{
    public long Version { get; init; }
    public Guid ScrapReasonCodeId { get; init; }
    public string? Comment { get; init; }
}
