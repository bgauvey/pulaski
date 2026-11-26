namespace Pulaski.Contracts.Setup;

/// <summary>A site's setup checklist (#139, D-126): every step in order, what is done, and whether it is ready for its first job.</summary>
public sealed record SetupChecklistDto
{
    public Guid? SiteId { get; init; }
    public string? SiteCode { get; init; }
    public IReadOnlyList<SetupStepDto> Steps { get; init; } = [];
    public int RequiredDone { get; init; }
    public int RequiredTotal { get; init; }

    /// <summary>Every required step done.</summary>
    public bool Ready { get; init; }
}

public sealed record SetupStepDto
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Why { get; init; }
    public required string Href { get; init; }

    /// <summary>The view that opens the step's screen.</summary>
    public required string Permission { get; init; }
    public bool Required { get; init; }

    /// <summary><c>done</c>, <c>to_do</c> or <c>not_needed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>How many there are (lines, materials…), once done.</summary>
    public int Count { get; init; }
    public string? MarkedBy { get; init; }
    public DateTimeOffset? MarkedAt { get; init; }
}

public sealed record MarkSetupStepRequest
{
    /// <summary>True: not needed at this site; false: needed after all.</summary>
    public bool NotNeeded { get; init; }
}
