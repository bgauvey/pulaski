using Pulaski.Contracts.Maintenance;
using Pulaski.Domain.Maintenance;

namespace Pulaski.Application.Maintenance;

/// <summary>A line or machine as a maintenance request needs it.</summary>
public sealed record MaintenanceEquipment(Guid Id, Guid? SiteId, string Code, string Level, bool IsActive);

/// <summary>A stop as a request raised from it needs it: its key and equipment.</summary>
public sealed record MaintenanceStop(Guid Id, DateTimeOffset StartedAt, Guid EquipmentId);

/// <summary>Write side for maintenance requests (#127, D-115): runs inside the current unit of work.</summary>
public interface IMaintenanceRepository
{
    /// <summary>The equipment, locked (as ingest and andon lock it), so two requests from one of its stops serialize.</summary>
    Task<MaintenanceEquipment?> LockEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken);

    /// <summary>The stop, current (not replaced by an adjustment); null when there is none.</summary>
    Task<MaintenanceStop?> GetStopAsync(Guid id, DateTimeOffset startedAt, CancellationToken cancellationToken);

    /// <summary>The request still open (not closed) raised from the stop, locked; null when there is none.</summary>
    Task<MaintenanceRequest?> FindOpenForStopForUpdateAsync(Guid downtimeId, CancellationToken cancellationToken);

    Task<long> NextNumberAsync(CancellationToken cancellationToken);

    Task<MaintenanceRequest?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task InsertAsync(MaintenanceRequest request, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateAsync(MaintenanceRequest request, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side for maintenance requests (#127).</summary>
public interface IMaintenanceReadStore
{
    /// <summary>
    /// The site's requests, newest first: those not closed when <paramref name="openOnly"/>, else those raised in the period; for a piece of
    /// equipment and what is below it when given. Each with its line. Waiting time and age are left for the handler.
    /// </summary>
    Task<IReadOnlyList<MaintenanceRequestDto>> ListAsync(
        Guid siteId, bool openOnly, DateTimeOffset? from, DateTimeOffset? until, Guid? equipmentId, CancellationToken cancellationToken);
}
