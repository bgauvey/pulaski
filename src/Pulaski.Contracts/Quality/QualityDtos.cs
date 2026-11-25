using Pulaski.Contracts.Common;
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

    /// <summary>The Nelson rules (1–8) its chart checks; 1 is always among them.</summary>
    public IReadOnlyList<int> RunRules { get; init; } = [1, 2, 5, 6];

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

    /// <summary>The Nelson rules its chart checks; rule 1 is always added. Left out, they stay.</summary>
    public IReadOnlyList<int>? RunRules { get; init; }
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
    public MessageDto? NotEnough { get; init; }
    public IReadOnlyList<MessageDto> Warnings { get; init; } = [];

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

    /// <summary>The characteristic's run rules it breaks against the limits worked out.</summary>
    public IReadOnlyList<int> Rules { get; init; } = [];
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

    /// <summary>The views the chart can show (#198): <c>location</c> (the default), <c>range</c>, <c>std_dev</c> or <c>moving_range</c>.</summary>
    public IReadOnlyList<string> Views { get; init; } = [];

    /// <summary>Results the chart can't take (a size that doesn't fit, no values), left off.</summary>
    public int Unusable { get; init; }

    /// <summary>More results than a chart shows: only the latest <see cref="MaxPoints"/> are.</summary>
    public bool IsTruncated { get; init; }
    public int MaxPoints { get; init; }

    /// <summary>The Nelson rules the chart checks.</summary>
    public IReadOnlyList<int> RunRules { get; init; } = [];

    /// <summary>How capable the charted results are against the material's released specification (D-099).</summary>
    public CapabilityDto Capability { get; init; } = new();

    /// <summary>The periods of limits the points use.</summary>
    public IReadOnlyList<ControlLimitDto> Limits { get; init; } = [];
}

/// <summary>
/// A chart's capability (D-099): for a measured characteristic Cp/Cpk (within-subgroup sigma) and Pp/Ppk (overall), judged by Cpk and Ppk
/// (capable ≥ 1.33, marginal ≥ 1.00, else not capable); for an inspected one its defective rate. Figures are null with <see cref="NotEnough"/>.
/// </summary>
public sealed record CapabilityDto
{
    /// <summary>The released specification version it is measured against; null without one.</summary>
    public int? SpecVersion { get; init; }
    public decimal? Lsl { get; init; }
    public decimal? Target { get; init; }
    public decimal? Usl { get; init; }
    public int? MaxDefects { get; init; }
    public int Subgroups { get; init; }
    public double? Mean { get; init; }
    public double? SigmaWithin { get; init; }
    public double? SigmaOverall { get; init; }
    public double? Cp { get; init; }
    public double? Cpk { get; init; }
    public double? Pp { get; init; }
    public double? Ppk { get; init; }

    /// <summary><c>capable</c>, <c>marginal</c> or <c>not_capable</c>, by Cpk; and by Ppk.</summary>
    public string? Band { get; init; }
    public string? PerformanceBand { get; init; }

    /// <summary>Inspected characteristics: defectives (p, np) or defects (c, u) a unit inspected, and in parts per million.</summary>
    public double? DefectRate { get; init; }
    public double? Ppm { get; init; }
    public int Inspected { get; init; }
    public int Defects { get; init; }

    /// <summary>Why there are no figures.</summary>
    public MessageDto? NotEnough { get; init; }

    /// <summary>The charted results break the rules: the process isn't stable, so capability says little.</summary>
    public bool IsUnstable { get; init; }
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

    /// <summary>
    /// The subgroup's measurements, its range and standard deviation (#198, subgroups of 2 or more), and both views' limits in force for it:
    /// the chart's own dispersion as stored, the other from the same sigma (<c>DispersionLimits</c>).
    /// </summary>
    public IReadOnlyList<double> Values { get; init; } = [];
    public double? Range { get; init; }
    public double? StdDev { get; init; }
    public double? RangeCenter { get; init; }
    public double? RangeUcl { get; init; }
    public double? RangeLcl { get; init; }
    public double? StdDevCenter { get; init; }
    public double? StdDevUcl { get; init; }
    public double? StdDevLcl { get; init; }
    public bool IsRangeBeyondLimits { get; init; }
    public bool IsStdDevBeyondLimits { get; init; }

    /// <summary>The characteristic's run rules this point completes a break of (D-099), rule 1 (beyond the limits) among them.</summary>
    public IReadOnlyList<int> Rules { get; init; } = [];
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

    /// <summary>
    /// When it is sampled in a run (#136): at the start, and/or every so many hours or units (in the job's unit, not both), with a grace in
    /// minutes before a due sample is overdue. Nothing set: no plan.
    /// </summary>
    public bool SampleAtStart { get; init; }
    public decimal? SampleEveryHours { get; init; }
    public decimal? SampleEveryUnits { get; init; }
    public int? SampleGraceMinutes { get; init; }
}

public sealed record SpecProblemDto(string Code, string Message, Guid? ItemId)
{
    /// <summary><see cref="Message"/>'s English template and values (#189), for the reader's language; null template: show the message.</summary>
    public string? Template { get; init; }

    public IReadOnlyList<object?> Values { get; init; } = [];
}

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
    public IReadOnlyList<MessageDto> Notes { get; init; } = [];

    /// <summary>The run rules this result broke on its control chart (D-099); empty when none, or the chart has no limits.</summary>
    public IReadOnlyList<int> SpcRules { get; init; } = [];

    /// <summary>The SPC alert it raised or was added to.</summary>
    public Guid? SpcAlertId { get; init; }
}

/// <summary>A characteristic sampled automatically from a process-value tag on a line (D-099).</summary>
public sealed record TagBindingDto
{
    public Guid Id { get; init; }
    public Guid CharacteristicId { get; init; }

    /// <summary>Null once its tag was deleted (it is then switched off).</summary>
    public Guid? EquipmentTagId { get; init; }
    public string? TagAddress { get; init; }
    public string? EquipmentCode { get; init; }
    public Guid LineId { get; init; }
    public string LineCode { get; init; } = "";
    public Guid SiteId { get; init; }
    public string SiteCode { get; init; } = "";
    public int IntervalMinutes { get; init; }
    public bool IsActive { get; init; }

    /// <summary>The last interval end dealt with.</summary>
    public DateTimeOffset SampledThrough { get; init; }
    public long Version { get; init; }
}

/// <summary>A process-value tag that can be bound.</summary>
public sealed record ProcessValueTagDto
{
    public Guid Id { get; init; }
    public string SourceAddress { get; init; } = "";
    public string EquipmentCode { get; init; } = "";
    public string? LineCode { get; init; }
    public string SiteCode { get; init; } = "";
    public string? UomCode { get; init; }
}

public sealed record AddTagBindingRequest
{
    public Guid EquipmentTagId { get; init; }
    public int IntervalMinutes { get; init; } = 15;
}

public sealed record UpdateTagBindingRequest
{
    public long Version { get; init; }
    public int IntervalMinutes { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>An SPC alert (D-099): a chart that signalled, open until someone closes it with a cause and the action taken.</summary>
public sealed record SpcAlertDto
{
    public Guid Id { get; init; }
    public Guid SiteId { get; init; }
    public Guid CharacteristicId { get; init; }
    public string CharacteristicCode { get; init; } = "";
    public string CharacteristicName { get; init; } = "";
    public Guid MaterialId { get; init; }
    public string MaterialCode { get; init; } = "";
    public Guid? EquipmentId { get; init; }
    public string? EquipmentCode { get; init; }

    /// <summary><c>open</c> or <c>closed</c>.</summary>
    public string Status { get; init; } = "open";

    /// <summary>Every run rule broken while it was open.</summary>
    public IReadOnlyList<int> Rules { get; init; } = [];

    /// <summary>How many results broke a rule while it was open.</summary>
    public int Breaks { get; init; }
    public DateTimeOffset FirstSampleAt { get; init; }
    public DateTimeOffset LastSampleAt { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? ClosedBy { get; init; }
    public string? Cause { get; init; }
    public string? Action { get; init; }

    /// <summary>The results that broke its rules, oldest first; only when one alert is read.</summary>
    public IReadOnlyList<SpcViolationDto> Violations { get; init; } = [];
}

/// <summary>A result that broke run rules, for an alert.</summary>
public sealed record SpcViolationDto
{
    public Guid SampleId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public IReadOnlyList<int> Rules { get; init; } = [];
    public decimal? Mean { get; init; }
    public int? Inspected { get; init; }
    public int? Defects { get; init; }
    public string? LotLabel { get; init; }
    public string? JobNumber { get; init; }
}

/// <summary>Closes an alert with what caused the signal (a reason of kind <c>spc_cause</c>) and what was done.</summary>
public sealed record CloseSpcAlertRequest
{
    public Guid CauseReasonCodeId { get; init; }
    public string Action { get; init; } = "";
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

/// <summary>
/// A sample a running run owes for a spec item's plan (#136, D-121): the job and line, the characteristic, how it stands (<c>overdue</c>,
/// <c>due</c>, <c>upcoming</c>, <c>done</c>), when the latest fell due, how many earlier were missed, and when (or after how many more units of
/// the job's unit) the next falls due.
/// </summary>
public sealed record DueSampleDto
{
    public required Guid RunId { get; init; }
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required Guid LineId { get; init; }
    public required string LineCode { get; init; }
    public required Guid CharacteristicId { get; init; }
    public required string CharacteristicCode { get; init; }
    public required string CharacteristicName { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int Missed { get; init; }
    public DateTimeOffset? NextAt { get; init; }
    public decimal? NextAfterUnits { get; init; }
    public string? UomCode { get; init; }
    public int Taken { get; init; }
    public bool AtStart { get; init; }
    public decimal? EveryHours { get; init; }
    public decimal? EveryUnits { get; init; }
    public string? SiteTimeZone { get; init; }
}
