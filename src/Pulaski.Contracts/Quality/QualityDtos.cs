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
}

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
