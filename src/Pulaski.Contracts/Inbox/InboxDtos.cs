namespace Pulaski.Contracts.Inbox;

/// <summary>What waits for the signed-in user to decide at their sites (#123): each item once, oldest first within its kind.</summary>
public sealed record InboxDto
{
    public IReadOnlyList<InboxItemDto> Items { get; init; } = [];
}

/// <summary>
/// One decision waiting (#123). <see cref="Kind"/> is <c>partial_batch</c>, <c>rate_proposal</c>, <c>erp_change</c>, <c>spc_alert</c> or
/// <c>labor</c>; <see cref="Link"/> is the page, relative to the UI, where it is decided.
/// </summary>
public sealed record InboxItemDto
{
    public required string Kind { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public required Guid SiteId { get; init; }
    public required string SiteCode { get; init; }

    /// <summary>Since when it has waited.</summary>
    public DateTimeOffset? Since { get; init; }
    public required string Link { get; init; }
}
