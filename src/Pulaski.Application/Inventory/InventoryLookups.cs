using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// The site's open jobs' remaining components against available stock (D-029), for one line or
/// department and everything under it when given, and for jobs needed within the horizon.
/// </summary>
public sealed record GetShortagesQuery(Guid SiteId, Guid? EquipmentId = null, int? HorizonDays = null) : IQuery<ShortageReportDto>;

/// <summary>
/// The site's lot movements, newest first (D-029). Without a range, the last 7 days. Pass the last
/// entry's time and id to read the next page.
/// </summary>
public sealed record ListMovementsQuery(
    Guid SiteId,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Kind = null,
    Guid? MaterialId = null,
    Guid? LocationId = null,
    Guid? JobId = null,
    string? Actor = null,
    string? Search = null,
    DateTimeOffset? BeforeAt = null,
    Guid? BeforeId = null) : IQuery<JournalPageDto>;

internal sealed class GetShortagesHandler(IInventoryReadStore store, IUnitReadStore units, TimeProvider time)
    : IQueryHandler<GetShortagesQuery, ShortageReportDto>
{
    public async Task<ShortageReportDto> HandleAsync(GetShortagesQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.SiteId != Guid.Empty, nameof(query.SiteId), "Required.")
            .Require(query.HorizonDays is null or (> 0 and <= 365), nameof(query.HorizonDays), "Between 1 and 365 days.")
            .ThrowIfInvalid();

        var now = time.GetUtcNow();
        var before = query.HorizonDays is { } days ? now.AddDays(days) : (DateTimeOffset?)null;
        var rows = await store.ListComponentDemandAsync(query.SiteId, query.EquipmentId, before, cancellationToken);
        var materialIds = rows.Select(r => r.MaterialId).Distinct().ToList();
        var allUnits = await units.GetUnitsAsync(cancellationToken);
        var conversions = await units.GetConversionsAsync(materialIds, cancellationToken);

        // A job may plan one component in several units: convert each to the base unit, then take what was consumed off once.
        List<InventoryWarningDto> problems = [];
        List<ComponentDemand> demand = [];
        foreach (var need in rows.GroupBy(r => (r.JobId, r.MaterialId)))
        {
            var first = need.First();
            var converter = UnitConversions.For(first.MaterialId, allUnits, conversions);
            var planned = 0m;
            foreach (var row in need)
            {
                if (converter.Factor(row.UomId, row.BaseUomId) is { } factor)
                {
                    planned += Math.Round(row.PlannedQty * factor, 6, MidpointRounding.AwayFromZero);
                }
                else
                {
                    problems.Add(new("uom_mismatch",
                        $"{row.JobNumber}: {row.MaterialCode} is planned in {row.UomCode}, which doesn't convert to {row.BaseUomCode}; it was left out."));
                }
            }

            demand.Add(new ComponentDemand(first.JobId, first.JobNumber, first.MaterialId, first.NeededFrom, Math.Max(0m, planned - first.ConsumedQty)));
        }

        var stock = (await store.ListAvailabilityAsync(query.SiteId, materialIds, cancellationToken)).ToDictionary(a => a.MaterialId);
        var jobs = rows.GroupBy(r => r.JobId).ToDictionary(g => g.Key, g => g.First());
        var materials = rows.GroupBy(r => r.MaterialId).ToDictionary(g => g.Key, g => g.First());
        var results = ShortageCalculator.Calculate(demand, stock.ToDictionary(s => s.Key, s => s.Value.Available));

        return new ShortageReportDto
        {
            SiteId = query.SiteId,
            CalculatedAt = now,
            Problems = problems,
            Materials = [.. results
                .Select(r => ToDto(r, materials[r.MaterialId], stock.GetValueOrDefault(r.MaterialId), jobs))
                .OrderByDescending(m => m.Shortfall > 0)
                .ThenBy(m => m.FirstShortAt is null)
                .ThenBy(m => m.FirstShortAt)
                .ThenBy(m => m.MaterialCode, StringComparer.Ordinal)],
        };
    }

    private static MaterialShortageDto ToDto(
        MaterialShortage shortage, ComponentDemandRow material, MaterialAvailabilityRow? stock, Dictionary<Guid, ComponentDemandRow> jobs) => new()
        {
            MaterialId = shortage.MaterialId,
            MaterialCode = material.MaterialCode,
            MaterialName = material.MaterialName,
            BaseUomCode = material.BaseUomCode,
            Available = shortage.Available,
            Held = stock?.Held ?? 0m,
            Demand = shortage.Demand,
            Shortfall = shortage.Shortfall,
            FirstShortJobId = shortage.FirstShort?.Demand.JobId,
            FirstShortJobNumber = shortage.FirstShort?.Demand.JobNumber,
            FirstShortAt = shortage.FirstShort?.Demand.NeededFrom,
            Lines = [.. shortage.Lines.Select(l =>
            {
                var job = jobs[l.Demand.JobId];
                return new ShortageLineDto
                {
                    JobId = job.JobId,
                    JobNumber = job.JobNumber,
                    JobStatus = job.JobStatus,
                    LineCode = job.LineCode,
                    WorkOrderNumber = job.WorkOrderNumber,
                    NeededFrom = l.Demand.NeededFrom,
                    Remaining = l.Demand.Remaining,
                    Cumulative = l.Cumulative,
                    Uncovered = l.Uncovered,
                };
            })],
        };
}

internal sealed class ListMovementsHandler(IInventoryReadStore store, TimeProvider time) : IQueryHandler<ListMovementsQuery, JournalPageDto>
{
    private static readonly TimeSpan DefaultRange = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public Task<JournalPageDto> HandleAsync(ListMovementsQuery query, CancellationToken cancellationToken)
    {
        var to = query.To ?? time.GetUtcNow();
        var from = query.From ?? to - DefaultRange;
        new Validator()
            .Require(query.SiteId != Guid.Empty, nameof(query.SiteId), "Required.")
            .Require(from < to, nameof(query.From), "Must be before To.")
            .Require(to - from <= MaxRange, nameof(query.From), "The range can be at most 366 days.")
            .Require((query.BeforeAt is null) == (query.BeforeId is null), nameof(query.BeforeId), "Give both BeforeAt and BeforeId, or neither.")
            .Require(query.Actor is null || query.Actor.Length <= 200, nameof(query.Actor), "At most 200 characters.")
            .ThrowIfInvalid();

        return store.ListMovementsAsync(
            new JournalFilter(
                query.SiteId, from, to, Blank(query.Kind), query.MaterialId, query.LocationId, query.JobId, Blank(query.Actor), Blank(query.Search),
                query.BeforeAt, query.BeforeId),
            cancellationToken);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
