using Pulaski.Contracts.Common;

namespace Pulaski.Contracts.Approvals;

/// <summary>A site's release approval settings (#138, D-122): which kinds need a second person's approval to be released.</summary>
public sealed record ReleaseApprovalSettingsDto
{
    public required Guid SiteId { get; init; }
    public bool Processes { get; init; }
    public bool Boms { get; init; }
    public bool Specs { get; init; }
    public long Version { get; init; }
}

public sealed record SetReleaseApprovalSettingsRequest
{
    public long Version { get; init; }
    public bool Processes { get; init; }
    public bool Boms { get; init; }
    public bool Specs { get; init; }
}

/// <summary>A draft submitted for release, and what became of it (#138): who submitted it and why, who decided and why.</summary>
public sealed record ReleaseRequestDto
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required Guid DocumentId { get; init; }
    public required Guid VersionId { get; init; }
    public int VersionNumber { get; init; }

    /// <summary><c>pending</c>, <c>approved</c>, <c>returned</c> or <c>withdrawn</c>.</summary>
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public string? SubmittedByName { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecidedByName { get; init; }
    public string? DecisionNote { get; init; }

    /// <summary>Whether the reader may decide it: they hold the approve permission at one of its sites and didn't submit or edit it.</summary>
    public bool MayDecide { get; init; }

    /// <summary>Whether the reader submitted it, and so may withdraw it.</summary>
    public bool MayWithdraw { get; init; }
}

/// <summary>Whether a document's release needs approval, and its requests, latest first.</summary>
public sealed record ReleaseStatusDto
{
    public bool ApprovalRequired { get; init; }
    public IReadOnlyList<ReleaseRequestDto> Requests { get; init; } = [];
}

public sealed record SubmitReleaseRequest
{
    public long Version { get; init; }
    public string? Note { get; init; }
}

public sealed record DecideReleaseRequest
{
    public string? Comment { get; init; }
}

public sealed record ReleaseDecisionResponse(Guid Id, string Status, IReadOnlyList<MessageDto> Notes);
