using System.Text.RegularExpressions;
using Pulaski.Domain.Inventory;

namespace Pulaski.Domain.Instructions;

/// <summary>Where an instruction's version stands (#126, D-116). Values match <c>core.work_instruction_version.status</c>.</summary>
public static class InstructionVersionStatus
{
    public const string Draft = "draft";
    public const string Released = "released";
    public const string Obsolete = "obsolete";
}

/// <summary>What an instruction is shown for (#126). Values match <c>core.work_instruction_target.kind</c>.</summary>
public static class InstructionTargetKinds
{
    /// <summary>An operation of a process, by the process and the operation's number, whatever its version.</summary>
    public const string Operation = "operation";
    public const string Material = "material";
    public const string Line = "line";

    public static IReadOnlyList<string> All { get; } = [Operation, Material, Line];
}

/// <summary>
/// A work instruction (#126a, D-116): a document of a site with a fixed code, written and released in versions. Switched off rather than
/// deleted, since jobs show it and checklists answered from it refer to it.
/// </summary>
public sealed partial record WorkInstruction(Guid Id, Guid SiteId, string Code, string Title, bool IsActive)
{
    public const int MaxTitle = 200;

    /// <exception cref="DomainRuleException">The code or title can't be used.</exception>
    public static WorkInstruction Create(Guid id, Guid siteId, string? code, string? title)
    {
        var trimmed = (code ?? "").Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(trimmed))
        {
            throw new DomainRuleException("An instruction's code is 1 to 30 letters, digits, '-', '_' and '.', such as WI-EXT-001.");
        }

        return new WorkInstruction(id, siteId, trimmed, "", true).Edit(title, true);
    }

    /// <exception cref="DomainRuleException">The title can't be used.</exception>
    public WorkInstruction Edit(string? title, bool isActive)
    {
        var trimmed = (title ?? "").Trim();
        return trimmed.Length is 0 or > MaxTitle
            ? throw new DomainRuleException($"Give the instruction a title of at most {MaxTitle} characters.")
            : this with { Title = trimmed, IsActive = isActive };
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_.-]{0,29}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// One version of an instruction (#126a): its text, written while a draft and fixed once released. Releasing a draft makes the version
/// released before it obsolete; the next draft starts from the released text and files.
/// </summary>
public sealed record InstructionVersion(
    Guid Id, Guid InstructionId, int Number, string Status, string Body, DateTimeOffset? ReleasedAt = null, string? ReleasedBy = null, string? Note = null)
{
    public const int MaxBody = 20_000;
    public const int MaxNote = 1000;

    /// <summary>Its setup and first-article checklists (#126c), at most one each; released with the text and fixed with it.</summary>
    public IReadOnlyList<Checklist> Checklists { get; init; } = [];

    public bool IsDraft => Status == InstructionVersionStatus.Draft;

    public static InstructionVersion FirstDraft(Guid id, Guid instructionId) => new(id, instructionId, 1, InstructionVersionStatus.Draft, "");

    /// <summary>The draft's text, and its checklists when given (null keeps them).</summary>
    /// <exception cref="DomainRuleException">It isn't a draft, the text is too long, or a checklist isn't right.</exception>
    public InstructionVersion SaveDraft(string? body, IReadOnlyList<Checklist>? checklists = null)
    {
        EnsureDraft();
        var text = (body ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        return text.Length > MaxBody
            ? throw new DomainRuleException($"Keep the instruction to {MaxBody} characters; put the rest in an attached file.")
            : this with { Body = text, Checklists = checklists is null ? Checklists : Instructions.Checklists.Check(checklists) };
    }

    /// <summary>Fixes the version, as of <paramref name="at"/> by <paramref name="by"/> with an optional <paramref name="note"/>: jobs show it from now on.
    /// A version needs text, a file (<paramref name="files"/>: how many the draft has) or a checklist.</summary>
    /// <exception cref="DomainRuleException">It isn't a draft, it is empty, or the note is too long.</exception>
    public InstructionVersion Release(DateTimeOffset at, string by, string? note, int files)
    {
        EnsureDraft();
        if (Body.Trim().Length == 0 && files == 0 && Checklists.Count == 0)
        {
            throw new DomainRuleException("Write the instruction, attach a file or add a checklist before releasing it.");
        }

        var trimmed = note?.Trim();
        return trimmed is { Length: > MaxNote }
            ? throw new DomainRuleException($"Keep the note to {MaxNote} characters.")
            : this with { Status = InstructionVersionStatus.Released, ReleasedAt = at, ReleasedBy = by, Note = string.IsNullOrEmpty(trimmed) ? null : trimmed };
    }

    /// <summary>Replaced by a newer release.</summary>
    public InstructionVersion MakeObsolete() => this with { Status = InstructionVersionStatus.Obsolete };

    /// <summary>The next draft, from this version's text and checklists.</summary>
    public InstructionVersion NextDraft(Guid id) => new(id, InstructionId, Number + 1, InstructionVersionStatus.Draft, Body) { Checklists = Checklists };

    /// <exception cref="DomainRuleException">It isn't a draft.</exception>
    public void EnsureDraft()
    {
        if (!IsDraft)
        {
            throw new DomainRuleException($"Version {Number} is {UserMessage.Code(Status)}; start a new draft to change it.");
        }
    }
}

/// <summary>A file of an instruction's version (#126a): PDF, PNG or JPEG, recognized by its first bytes, at most 10 MB.</summary>
public static class InstructionFiles
{
    public const int MaxBytes = LotDocumentFiles.MaxBytes;
    public const int MaxPerVersion = 20;
    public const int MaxName = 200;

    /// <returns>The file's content type.</returns>
    /// <exception cref="DomainRuleException">It is empty, too large, of another kind, badly named, or one too many.</exception>
    public static string Check(string? fileName, ReadOnlySpan<byte> content, int filesAlready)
    {
        var name = (fileName ?? "").Trim();
        if (name.Length is 0 or > MaxName)
        {
            throw new DomainRuleException($"Give the file a name of at most {MaxName} characters.");
        }

        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new DomainRuleException($"A file is at most {MaxBytes / 1024 / 1024} MB, and not empty.");
        }

        if (filesAlready >= MaxPerVersion)
        {
            throw new DomainRuleException($"A version has at most {MaxPerVersion} files.");
        }

        return LotDocumentFiles.Sniff(content) ?? throw new DomainRuleException($"{name} isn't a PDF, PNG or JPEG file.");
    }
}

/// <summary>What an instruction is shown for (#126a): one of a process's operations, a material, or a line. Ended rather than deleted.</summary>
public sealed record InstructionTarget(
    Guid Id, Guid InstructionId, string Kind, Guid? ProcessId = null, int? OperationNumber = null, Guid? MaterialId = null, Guid? EquipmentId = null,
    bool IsActive = true)
{
    /// <exception cref="DomainRuleException">The kind is unknown, or what it names doesn't fit it.</exception>
    public static InstructionTarget Create(Guid id, Guid instructionId, string? kind, Guid? processId, int? operationNumber, Guid? materialId, Guid? equipmentId)
    {
        var chosen = (kind ?? "").Trim();
        return chosen switch
        {
            InstructionTargetKinds.Operation when processId is { } p && operationNumber is > 0 and var n => new(id, instructionId, chosen, p, n),
            InstructionTargetKinds.Operation => throw new DomainRuleException("Choose the process and the operation's number."),
            InstructionTargetKinds.Material when materialId is { } m => new(id, instructionId, chosen, MaterialId: m),
            InstructionTargetKinds.Material => throw new DomainRuleException("Choose the material."),
            InstructionTargetKinds.Line when equipmentId is { } e => new(id, instructionId, chosen, EquipmentId: e),
            InstructionTargetKinds.Line => throw new DomainRuleException("Choose the line."),
            _ => throw new DomainRuleException($"'{chosen}' isn't what an instruction is for; use operation, material or line."),
        };
    }

    /// <summary>The same thing named again: what an instruction can't be attached to twice.</summary>
    public bool SameAs(InstructionTarget other) =>
        Kind == other.Kind && ProcessId == other.ProcessId && OperationNumber == other.OperationNumber && MaterialId == other.MaterialId && EquipmentId == other.EquipmentId;
}

/// <summary>
/// Which released instructions a job shows (#126b): one entry per instruction, whatever number of its targets match, with the kinds that
/// matched; an operation's first, then a line's, then a material's, each by code. Pure.
/// </summary>
public static class JobInstructions
{
    private static readonly string[] Order = [InstructionTargetKinds.Operation, InstructionTargetKinds.Line, InstructionTargetKinds.Material];

    public static int Rank(string kind) => Array.IndexOf(Order, kind) is var i and >= 0 ? i : Order.Length;

    public static IReadOnlyList<(Guid InstructionId, string[] Kinds)> Gather(IEnumerable<(Guid InstructionId, string Code, string Kind)> matches) =>
        [.. matches
            .GroupBy(m => m.InstructionId)
            .Select(g => (Id: g.Key, g.First().Code, Kinds: g.Select(m => m.Kind).Distinct().OrderBy(Rank).ToArray()))
            .OrderBy(g => Rank(g.Kinds[0])).ThenBy(g => g.Code, StringComparer.Ordinal)
            .Select(g => (g.Id, g.Kinds))];
}
