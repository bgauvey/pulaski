using Pulaski.Application.Inbox;
using Pulaski.Contracts.Inbox;

namespace Pulaski.Testing;

/// <summary>In-memory inbox reads (#123): the test adds sites and the items waiting at them.</summary>
public sealed class InMemoryInbox : IInboxReadStore
{
    public List<(Guid Id, string Code)> Sites { get; } = [];

    public List<InboxItemDto> Items { get; } = [];

    public Task<IReadOnlyList<(Guid Id, string Code)>> ListSitesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Guid Id, string Code)>>([.. Sites]);

    public Task<IReadOnlyList<InboxItemDto>> ListAsync(string kind, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InboxItemDto>>([.. Items.Where(i => i.Kind == kind && siteIds.Contains(i.SiteId))]);

    /// <summary>NCR actions (#133b): whose they are and whether they are overdue.</summary>
    public List<(Guid? OwnerId, bool Overdue, InboxItemDto Item)> NcrActions { get; } = [];

    public Task<IReadOnlyList<InboxItemDto>> ListNcrActionsAsync(Guid userId, IReadOnlyCollection<Guid> manageSiteIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InboxItemDto>>(
            [.. NcrActions.Where(a => a.OwnerId == userId || (a.Overdue && manageSiteIds.Contains(a.Item.SiteId))).Select(a => a.Item)]);
}
