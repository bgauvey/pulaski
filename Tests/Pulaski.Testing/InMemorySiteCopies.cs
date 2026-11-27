using Pulaski.Application.EquipmentModel;

namespace Pulaski.Testing;

/// <summary>Site copies in memory (#140b): nothing to copy.</summary>
public sealed class InMemorySiteCopies : ISiteCopyRepository
{
    public Task<SiteCopyCounts> CountAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(new SiteCopyCounts(0, 0, 0, 0, 0, 0, 0, 0, 0));

    public Task<int> CopyShiftsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<int> CopyReasonsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<int> CopyPeopleListsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<int> CopySettingsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(0);
}
