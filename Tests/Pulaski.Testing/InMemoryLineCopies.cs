using Pulaski.Application.EquipmentModel;

namespace Pulaski.Testing;

/// <summary>Line copies in memory (#140): enough for the handlers to resolve; nothing to copy.</summary>
public sealed class InMemoryLineCopies : ILineCopyRepository
{
    public Task<LineCopySourceLine?> LoadAsync(Guid lineId, CancellationToken cancellationToken) => Task.FromResult<LineCopySourceLine?>(null);

    public Task<IReadOnlySet<string>> CodesAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<bool> LockParentAsync(Guid parentId, Guid siteId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task CopyEquipmentAsync(IReadOnlyList<LineCopyRow> rows, bool machineSettings, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Guid>> CopyCapabilitiesAsync(IReadOnlyList<LineCopyRow> rows, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task CopyStopCodesAsync(IReadOnlyList<LineCopyRow> rows, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CopyRequirementsAsync(IReadOnlyList<LineCopyRow> rows, Func<Guid> newId, DateTimeOffset at, string by, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
