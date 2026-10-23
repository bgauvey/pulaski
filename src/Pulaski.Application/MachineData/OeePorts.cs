using Pulaski.Application.Labor;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>A piece of equipment that tracks state, as the OEE report needs it; <see cref="ParentId"/> is the nearest above it that does too.</summary>
public sealed record OeeEquipmentRecord(
    Guid Id, string Code, string Name, Guid? ParentId, string StateDerivation, Guid? ConstraintEquipmentId, bool CanRunJob);

/// <summary>
/// A run in the report's period, with its job's material (none for a changeover or maintenance job) and the ideal rate of the
/// capability in effect when it started. A job that counts as planned downtime (planned maintenance) takes its time out of planned time.
/// </summary>
public sealed record OeeRunRecord(
    Guid RunId, Guid EquipmentId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, Guid? MaterialId, string? MaterialCode, string? MaterialName,
    Guid? JobUomId, decimal? IdealRatePerHour, Guid? RateUomId, string JobNumber, bool CountsAsPlannedDowntime = false);

/// <summary>Production reported into a run at one moment (D-035): processed is good and scrap together.</summary>
public sealed record OeeCountRecord(Guid RunId, DateTimeOffset OccurredAt, Guid? UomId, decimal Processed, decimal Defective);

public sealed record OeeReasonRecord(Guid Id, string Code, string Name, string? UtilizationClass);

/// <summary>Read side for the utilization and OEE report (D-052): the machine history of a period, untouched.</summary>
public interface IOeeReadStore
{
    Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's active equipment that tracks state, in tree order.</summary>
    Task<IReadOnlyList<OeeEquipmentRecord>> ListEquipmentAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The unit modes that are production time at the site: its own setting of a mode, else the enterprise default.</summary>
    Task<IReadOnlySet<int>> GetProductionModesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);

    /// <summary>Each equipment's state changes in <c>[windowStart, windowEnd)</c>, and its last one before.</summary>
    Task<IReadOnlyList<(Guid EquipmentId, StatePoint State)>> ListStatesAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>The stops overlapping <c>[windowStart, windowEnd)</c>.</summary>
    Task<IReadOnlyList<(Guid EquipmentId, StopSpan Stop)>> ListStopsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>The runs on the equipment overlapping <c>[windowStart, windowEnd)</c>.</summary>
    Task<IReadOnlyList<OeeRunRecord>> ListRunsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>Production reported on the equipment in <c>[windowStart, windowEnd)</c>; machine counts (D-050) aren't reports and are left out.</summary>
    Task<IReadOnlyList<OeeCountRecord>> ListCountsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    Task<IReadOnlyList<OeeReasonRecord>> ListReasonsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
