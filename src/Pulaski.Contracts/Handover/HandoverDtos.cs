namespace Pulaski.Contracts.Handover;

/// <summary>A site's shift handover (#124): each line's open items, and the notes of its current and previous shift.</summary>
public sealed record HandoverDto
{
    public required Guid SiteId { get; init; }
    public string? TimeZone { get; init; }
    public IReadOnlyList<HandoverLineDto> Lines { get; init; } = [];
}

public sealed record HandoverLineDto
{
    public required Guid LineId { get; init; }
    public required string LineCode { get; init; }
    public string? LineName { get; init; }

    /// <summary>The shift running now on the line, and the one before it (or, without shifts, today and yesterday).</summary>
    public required HandoverShiftDto Current { get; init; }
    public required HandoverShiftDto Previous { get; init; }

    /// <summary>Every item still open on the line, oldest first, whichever shift wrote it.</summary>
    public IReadOnlyList<HandoverEntryDto> OpenItems { get; init; } = [];
}

/// <summary>A shift as the handover names it, with what was written in it (notes and items, oldest first).</summary>
public sealed record HandoverShiftDto
{
    public required DateOnly ProductionDate { get; init; }

    /// <summary>The shift's code and name; null for a day without shifts.</summary>
    public string? ShiftCode { get; init; }
    public string? ShiftName { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public IReadOnlyList<HandoverEntryDto> Entries { get; init; } = [];
}

public sealed record HandoverEntryDto
{
    public required Guid Id { get; init; }
    public required Guid LineId { get; init; }

    /// <summary><c>note</c> or <c>open_item</c>.</summary>
    public required string Kind { get; init; }
    public required string Body { get; init; }
    public DateOnly ProductionDate { get; init; }
    public string? ShiftCode { get; init; }
    public DateTimeOffset WrittenAt { get; init; }
    public required string WrittenBy { get; init; }
    public string? WrittenByName { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? ClosedBy { get; init; }
    public string? ClosedByName { get; init; }
    public string? ClosingNote { get; init; }
}

public sealed record AddHandoverEntryRequest
{
    public Guid LineId { get; init; }

    /// <summary><c>note</c> or <c>open_item</c>.</summary>
    public string Kind { get; init; } = "note";
    public string Body { get; init; } = "";
}

public sealed record CloseHandoverItemRequest
{
    public string? Note { get; init; }
}
