namespace Pulaski.Contracts.Quality;

/// <summary>A nonconformance report in a list (#133, D-124).</summary>
public sealed record NcrSummaryDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required string Severity { get; init; }
    public required string Status { get; init; }
    public required string SourceKind { get; init; }
    public string? OwnerName { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public int OpenActions { get; init; }
    public int OverdueActions { get; init; }
}

/// <summary>One NCR with its links, investigation, actions and close.</summary>
public sealed record NcrDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public string? SiteTimeZone { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required string Severity { get; init; }
    public required string Status { get; init; }
    public required string SourceKind { get; init; }
    public Guid? SourceId { get; init; }
    public DateTimeOffset? SourceAt { get; init; }
    public Guid? OwnerUserId { get; init; }
    public string? OwnerName { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public string? RaisedByName { get; init; }
    public string? RootCause { get; init; }
    public IReadOnlyList<string> Whys { get; init; } = [];
    public string? Disposition { get; init; }
    public string? ClosingNote { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? ClosedByName { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<NcrLotDto> Lots { get; init; } = [];
    public IReadOnlyList<NcrRunDto> Runs { get; init; } = [];
    public IReadOnlyList<NcrActionDto> Actions { get; init; } = [];
}

/// <summary>A lot an NCR touches, with what is left of it, so its disposition can be carried out (#133b).</summary>
public sealed record NcrLotDto(Guid LotId, string Label, string MaterialCode, string Status, decimal Quantity, string UomCode, long Version);

public sealed record NcrRunDto(Guid RunId, string JobNumber, int RunNo, string LineCode);

public sealed record NcrActionDto
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public Guid? OwnerUserId { get; init; }
    public string? OwnerName { get; init; }
    public DateOnly? DueOn { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? DoneAt { get; init; }
    public string? DoneByName { get; init; }
    public string? Note { get; init; }
    public bool IsOverdue { get; init; }
}

/// <summary>Someone an NCR or an action can be given to: an active user holding a role at the site (or everywhere).</summary>
public sealed record NcrAssigneeDto(Guid Id, string Name);

public sealed record RaiseNcrRequest
{
    /// <summary>The site, for an NCR raised on its own or from a complaint; otherwise its source's.</summary>
    public Guid? SiteId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string Severity { get; init; } = "minor";
    public string SourceKind { get; init; } = "manual";
    public Guid? SourceId { get; init; }
    public DateTimeOffset? SourceAt { get; init; }
    public Guid? OwnerUserId { get; init; }
    public IReadOnlyList<Guid> LotIds { get; init; } = [];
}

public sealed record UpdateNcrRequest
{
    public long Version { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string Severity { get; init; } = "minor";
    public Guid? OwnerUserId { get; init; }
    public IReadOnlyList<Guid> AddLotIds { get; init; } = [];
}

public sealed record InvestigateNcrRequest
{
    public long Version { get; init; }
    public string? RootCause { get; init; }
    public IReadOnlyList<string> Whys { get; init; } = [];
    public bool Complete { get; init; }
}

public sealed record AddNcrActionRequest
{
    public string Kind { get; init; } = "corrective";
    public string? Description { get; init; }
    public Guid? OwnerUserId { get; init; }
    public DateOnly? DueOn { get; init; }
}

public sealed record FinishNcrActionRequest
{
    public string? Note { get; init; }
    public bool Cancel { get; init; }
}

public sealed record CloseNcrRequest
{
    public long Version { get; init; }
    public string? Disposition { get; init; }
    public string? Note { get; init; }
}

public sealed record NcrCreatedResponse(Guid Id, string Number);

/// <summary>Links a lot of the NCR's site by its number (#133b).</summary>
public sealed record LinkNcrLotRequest
{
    public string? LotNumber { get; init; }
}
