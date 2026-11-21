using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Andon;
using Pulaski.Domain.Andon;

namespace Pulaski.Application.Andon;

/// <summary>A line as an andon call needs it.</summary>
public sealed record AndonLine(Guid Id, Guid SiteId, string Code, bool CanRunJob, bool IsActive);

/// <summary>Write side for andon (#125): runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IAndonRepository
{
    Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The line, locked (as ingest and stop adjustments lock equipment), so two calls of a type on it serialize.</summary>
    Task<AndonLine?> LockLineAsync(Guid lineId, CancellationToken cancellationToken);

    Task<Versioned<AndonType>?> GetTypeForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> TypeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<long> InsertTypeAsync(AndonType type, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateTypeAsync(AndonType type, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The call of this type still open (not resolved) on the line, locked; null when there is none.</summary>
    Task<AndonCall?> FindOpenCallForUpdateAsync(Guid lineId, Guid typeId, CancellationToken cancellationToken);

    Task<AndonCall?> GetCallForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task InsertCallAsync(AndonCall andonCall, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateCallAsync(AndonCall andonCall, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side for andon (#125).</summary>
public interface IAndonReadStore
{
    Task<IReadOnlyList<AndonTypeDto>> ListTypesAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>
    /// The site's calls, newest first: those not resolved when <paramref name="openOnly"/>, else those raised in the period; on one line when
    /// given. <see cref="AndonCallDto.IsEscalated"/> and <see cref="AndonCallDto.WaitedSeconds"/> are left for the handler.
    /// </summary>
    Task<IReadOnlyList<AndonCallDto>> ListCallsAsync(
        Guid siteId, bool openOnly, DateTimeOffset? from, DateTimeOffset? until, Guid? lineId, CancellationToken cancellationToken);
}
