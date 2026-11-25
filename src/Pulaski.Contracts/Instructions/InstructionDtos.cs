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

    /// <summary>Its setup and first-article checklists (#126c), setup first.</summary>
    public IReadOnlyList<ChecklistDto> Checklists { get; init; } = [];
}

/// <summary>A checklist of a version (#126c): <c>setup</c> (before the job starts on a line) or <c>first_article</c> (before its first report there).</summary>
public sealed record ChecklistDto
{
    public required string Phase { get; init; }
    public IReadOnlyList<ChecklistItemDto> Items { get; init; } = [];

    /// <summary>A passed one counts only once someone else holding <c>checklists.sign_off</c> signed it off (#126d).</summary>
    public bool SecondSignOff { get; init; }
}

/// <summary>
/// One item, numbered from 1 in order: kind <c>yes_no</c> (optionally expecting yes or no), <c>number</c> (optionally within limits, with a unit),
/// <c>choice</c> (one of its choices) or <c>text</c>.
/// </summary>
public sealed record ChecklistItemDto
{
    public string Prompt { get; init; } = "";
    public string Kind { get; init; } = "yes_no";
    public bool Required { get; init; }
    public bool? Expected { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public string? Unit { get; init; }
    public IReadOnlyList<string>? Choices { get; init; }
}

/// <summary>One item's answer as kept: the item's number, the answer, and whether it is within what the item allows.</summary>
public sealed record ChecklistAnswerDto(int Item, string? Value, bool Ok);

/// <summary>
/// A checklist as a job on its line shows it (#126c): its items and how it stands there: null until answered, <c>passed</c> once an answer
/// passed, else <c>failed</c> (answer it again), with the latest answer.
/// </summary>
public sealed record JobChecklistDto
{
    public required string Phase { get; init; }
    public IReadOnlyList<ChecklistItemDto> Items { get; init; } = [];
    public bool SecondSignOff { get; init; }

    /// <summary>The answer shown: the one that counts, else one waiting for its sign-off, else the latest.</summary>
    public Guid? ResponseId { get; init; }
    public string? Result { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? CompletedByName { get; init; }
    public IReadOnlyList<ChecklistAnswerDto> Answers { get; init; } = [];

    /// <summary>Its second sign-off (#126d): null (none needed), <c>pending</c>, <c>signed</c> or <c>rejected</c>, with who, when and the note.</summary>
    public string? SignOff { get; init; }
    public DateTimeOffset? SignedOffAt { get; init; }
    public string? SignedOffByName { get; init; }
    public string? SignOffNote { get; init; }

    /// <summary>Whether it counts: passed, and signed off if it needs that.</summary>
    public bool Counts => Result == "passed" && SignOff is null or "signed";
}

/// <summary>A checklist as answered, for the job's history (#126d): by run, with its answers and sign-offs.</summary>
public sealed record ChecklistRecordDto
{
    public required Guid Id { get; init; }
    public Guid? RunId { get; init; }
    public int? RunNo { get; init; }
    public string LineCode { get; init; } = "";
    public string InstructionCode { get; init; } = "";
    public int VersionNumber { get; init; }
    public required string Phase { get; init; }
    public required string Result { get; init; }
    public IReadOnlyList<ChecklistItemDto> Items { get; init; } = [];
    public IReadOnlyList<ChecklistAnswerDto> Answers { get; init; } = [];
    public DateTimeOffset CompletedAt { get; init; }
    public string? CompletedByName { get; init; }
    public string? SignOff { get; init; }
    public DateTimeOffset? SignedOffAt { get; init; }
    public string? SignedOffByName { get; init; }
    public string? SignOffNote { get; init; }
}

/// <summary>A second sign-off given (<see cref="Approve"/>) or refused; a refusal needs a note.</summary>
public sealed record SignOffChecklistRequest
{
    public bool Approve { get; init; }
    public string? Note { get; init; }
}

public sealed record ChecklistSignedOffResponse(Guid Id, string SignOff);

/// <summary>A checklist answered at the job: each item's answer by its number (blank = not answered).</summary>
public sealed record CompleteChecklistRequest
{
    public required Guid InstructionId { get; init; }
    public IReadOnlyDictionary<int, string?> Answers { get; init; } = new Dictionary<int, string?>();
}

/// <summary>Whether the answers passed; a failed checklist is kept, and the job needs one that passes.</summary>
public sealed record ChecklistCompletedResponse(Guid Id, bool Passed, IReadOnlyList<int> FailedItems)
{
    /// <summary>It passed and now waits for its second sign-off (#126d).</summary>
    public bool WaitsForSignOff { get; init; }
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

    /// <summary>The draft's checklists (#126c); null keeps them as they are.</summary>
    public IReadOnlyList<ChecklistDto>? Checklists { get; init; }
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

/// <summary>
/// A released instruction as a job shows it (#126b, D-116): the version in force, its text and files, and why the job shows it (its process
/// operation, its line, its material; <see cref="ShownFor"/> holds those kinds).
/// </summary>
public sealed record JobInstructionDto
{
    public required Guid InstructionId { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required Guid VersionId { get; init; }
    public required int Number { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public string Body { get; init; } = "";
    public IReadOnlyList<InstructionFileDto> Files { get; init; } = [];

    /// <summary>The job's site's time zone, for when its checklists were answered.</summary>
    public string? SiteTimeZone { get; init; }

    /// <summary>Its checklists (#126c) and how each stands for the job on its line.</summary>
    public IReadOnlyList<JobChecklistDto> Checklists { get; init; } = [];

    /// <summary><c>operation</c>, <c>line</c> and/or <c>material</c>, in that order.</summary>
    public IReadOnlyList<string> ShownFor { get; init; } = [];
}
