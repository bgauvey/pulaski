using Pulaski.Application.Labor;
using Pulaski.Contracts.MachineData;
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

/// <summary>A reason the report names, with the groups it is filed under, top first (#86).</summary>
public sealed record OeeReasonRecord(Guid Id, string Code, string Name, string? UtilizationClass, IReadOnlyList<ReasonGroupDto>? Groups = null);

/// <summary>A material the report names, as it is called now.</summary>
public sealed record OeeMaterialRecord(Guid Id, string Code, string Name);

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

    Task<IReadOnlyList<OeeMaterialRecord>> ListMaterialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Every period of the site's OEE targets (D-073).</summary>
    Task<IReadOnlyList<OeeTarget>> ListTargetsAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>
/// A site's production day to roll up (#66): never rolled, marked by a late change to its history, or rolled before the site's settings last
/// changed. <see cref="Generation"/> and <see cref="DirtyAt"/> are as seen before rolling, so a change made meanwhile leaves it to roll again.
/// </summary>
public sealed record OeeDayToRoll(Guid SiteId, DateOnly ProductionDate, long Generation, DateTimeOffset? DirtyAt);

/// <summary>The daily OEE rollups (#66): a site's production days, each piece of equipment's windows of a day as the report adds them up.</summary>
public interface IOeeRollupStore
{
    /// <summary>The days of the period whose rollups are current: rolled since the site's settings last changed and not marked since.</summary>
    Task<IReadOnlySet<DateOnly>> ListCurrentDaysAsync(Guid siteId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    Task<IReadOnlyList<OeeWindowFigures>> ListFiguresAsync(IReadOnlyCollection<Guid> equipmentIds, IReadOnlyCollection<DateOnly> days, CancellationToken cancellationToken);

    /// <summary>
    /// Days of every site to roll, newest first: from <paramref name="keepDays"/> back to the day before yesterday (site-local), the last day whose
    /// shifts and output are all in. <paramref name="siteId"/> limits them to one site.
    /// </summary>
    Task<IReadOnlyList<OeeDayToRoll>> ListDaysToRollAsync(DateTimeOffset now, int keepDays, int limit, Guid? siteId, CancellationToken cancellationToken);
}

/// <summary>Write side for the daily OEE rollups (#66), inside the rolling transaction.</summary>
public interface IOeeRollupRepository
{
    /// <summary>
    /// Keeps each piece of equipment's windows of the day (an empty list for one with none), then records the day as rolled at the generation
    /// seen; it stays marked when it was marked again after <see cref="OeeDayToRoll.DirtyAt"/> was read.
    /// </summary>
    Task SaveDayAsync(OeeDayToRoll day, IReadOnlyCollection<Guid> equipmentIds, IReadOnlyList<OeeWindowFigures> figures, DateTimeOffset at, CancellationToken cancellationToken);
}
