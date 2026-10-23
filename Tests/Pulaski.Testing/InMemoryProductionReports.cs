using Pulaski.Application.Execution;
using Pulaski.Application.Labor;

namespace Pulaski.Testing;

/// <summary>In-memory counts, line calendars and run totals for production by shift (D-044).</summary>
public sealed class InMemoryProductionReports : IProductionReportReadStore
{
    public Dictionary<Guid, string> SiteTimeZones { get; } = [];

    public List<ProductionCountEntry> Counts { get; } = [];

    public Dictionary<Guid, EquipmentShifts> Calendars { get; } = [];

    public Dictionary<Guid, JobRunTotal> RunTotals { get; } = [];

    public Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(SiteTimeZones.GetValueOrDefault(siteId));

    public Task<IReadOnlyList<ProductionCountEntry>> ListCountsAsync(Guid siteId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProductionCountEntry>>([.. Counts.Where(c => c.OccurredAt >= windowStart && c.OccurredAt < windowEnd)]);

    public Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EquipmentShifts>>([.. equipmentIds.Where(Calendars.ContainsKey).Select(id => Calendars[id])]);

    public Task<IReadOnlyList<JobRunTotal>> ListRunTotalsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobRunTotal>>([.. jobIds.Where(RunTotals.ContainsKey).Select(id => RunTotals[id])]);
}
