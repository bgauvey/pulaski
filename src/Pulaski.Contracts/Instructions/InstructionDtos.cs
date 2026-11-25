namespace Pulaski.Contracts.Instructions;

/// <summary>A work instruction in a list (#126a, D-116): its released version and whether a draft waits.</summary>
public sealed record InstructionSummaryDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
    public int? ReleasedNumber { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public int? DraftNumber { get; init; }

    /// <summary>What it is shown for, by code: "EXTRUDE · 20" (a process's operation), "PVC-100", "EXT-01".</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];
}

/// <summary>A work instruction with its versions, files and what it is shown for (#126a).</summary>
public sealed record InstructionDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<InstructionVersionDto> Versions { get; init; } = [];
    public IReadOnlyList<InstructionTargetDto> Targets { get; init; } = [];
}

public sealed record InstructionVersionDto
{
    public required Guid Id { get; init; }
    public int Number { get; init; }

    /// <summary><c>draft</c>, <c>released</c> or <c>obsolete</c>.</summary>
    public required string Status { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public string? ReleasedBy { get; init; }
    public string? ReleasedByName { get; init; }
    public string? Note { get; init; }

    /// <summary>The version row's version, for saving a draft.</summary>
    public long Version { get; init; }
    public IReadOnlyList<InstructionFileDto> Files { get; init; } = [];
}

public sealed record InstructionFileDto
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public int SizeBytes { get; init; }
}

public sealed record InstructionTargetDto
{
    public required Guid Id { get; init; }

    /// <summary><c>operation</c>, <c>material</c> or <c>line</c>.</summary>
    public required string Kind { get; init; }
    public Guid? ProcessId { get; init; }
    public string? ProcessCode { get; init; }
    public int? OperationNumber { get; init; }
    public string? OperationName { get; init; }
    public Guid? MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    public Guid? EquipmentId { get; init; }
    public string? EquipmentCode { get; init; }
}

public sealed record CreateInstructionRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
}

public sealed record UpdateInstructionRequest
{
    public long Version { get; init; }
    public string Title { get; init; } = "";
    public bool IsActive { get; init; }
}

public sealed record SaveInstructionDraftRequest
{
    public long Version { get; init; }
    public string Body { get; init; } = "";
}

public sealed record ReleaseInstructionRequest
{
    public long Version { get; init; }
    public string? Note { get; init; }
}

public sealed record AddInstructionTargetRequest
{
    public string Kind { get; init; } = "";
    public Guid? ProcessId { get; init; }
    public int? OperationNumber { get; init; }
    public Guid? MaterialId { get; init; }
    public Guid? EquipmentId { get; init; }
}

/// <summary>What the instruction file limits are, for the upload form.</summary>
public static class InstructionFileFormat
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const string Accept = ".pdf,.png,.jpg,.jpeg,application/pdf,image/png,image/jpeg";
}
