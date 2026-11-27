using Pulaski.Application.EquipmentModel;

namespace Pulaski.Testing;

/// <summary>Line copies in memory (#140): enough for the handlers to resolve; nothing to copy.</summary>
public sealed class InMemoryLineCopies : ILineCopyRepository
{
    public Task<LineCopySourceLine?> LoadAsync(Guid lineId, CancellationToken cancellationToken) => Task.FromResult<LineCopySourceLine?>(null);

    public Task<IReadOnlySet<string>> CodesAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<Guid?> LockParentAsync(Guid parentId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<IReadOnlyList<(string Kind, string Code)>> UnmatchedAsync(Guid lineId, Guid targetSiteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(string Kind, string Code)>>([]);

    public Task CopyEquipmentAsync(
        IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, bool machineSettings, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Guid>> CopyCapabilitiesAsync(
        IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task CopyStopCodesAsync(IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CopyRequirementsAsync(
        IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, Func<Guid> newId, DateTimeOffset at, string by, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
