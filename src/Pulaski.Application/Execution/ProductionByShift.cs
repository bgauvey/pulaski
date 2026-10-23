using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Execution;
using Pulaski.Domain.Execution;

namespace Pulaski.Application.Execution;

/// <summary>Good and scrap by shift for a site's production days (D-044), at most <see cref="GetProductionByShiftHandler.MaxDays"/> apart.</summary>
public sealed record GetProductionByShiftQuery(Guid SiteId, DateOnly From, DateOnly To) : IQuery<ProductionByShiftDto>;

/// <summary>A row of reported output with what the report shows of its line, job and material.</summary>
public sealed record ProductionCountEntry(
    Guid EquipmentId, string LineCode, Guid JobId, string JobNumber, string? WorkOrderNumber, string MaterialCode, string MaterialName,
    Guid UomId, string UomCode, DateTimeOffset OccurredAt, string CountKind, decimal Quantity);

/// <summary>A job's good and scrap across all its runs (<c>core.job_run</c>), in its unit.</summary>
public sealed record JobRunTotal(Guid JobId, decimal GoodQty, decimal ScrapQty);

/// <summary>Read side for production by shift (D-044).</summary>
public interface IProductionReportReadStore
{
    Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's counts that occurred in <c>[windowStart, windowEnd)</c>.</summary>
    Task<IReadOnlyList<ProductionCountEntry>> ListCountsAsync(Guid siteId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>Where each line's working time comes from (D-038).</summary>
    Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);

    /// <summary>The jobs' run totals: good and scrap over all their runs.</summary>
    Task<IReadOnlyList<JobRunTotal>> ListRunTotalsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken);
}

internal sealed class GetProductionByShiftHandler(IProductionReportReadStore store) : IQueryHandler<GetProductionByShiftQuery, ProductionByShiftDto>
{
    public const int MaxDays = 93;

    public async Task<ProductionByShiftDto> HandleAsync(GetProductionByShiftQuery query, CancellationToken cancellationToken)
    {
        var (from, to) = query.From <= query.To ? (query.From, query.To) : (query.To, query.From);
        if (to.DayNumber - from.DayNumber > MaxDays)
        {
            from = to.AddDays(-MaxDays);
        }

        var timeZone = await store.GetSiteTimeZoneAsync(query.SiteId, cancellationToken) ?? throw new NotFoundException($"Site {query.SiteId} not found.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);

        // A shift of the first day may start the evening before it locally ends; one of the last day may run into the next.
        var counts = await store.ListCountsAsync(query.SiteId, Midnight(from.AddDays(-1), zone), Midnight(to.AddDays(2), zone), cancellationToken);
        var calendars = (await store.ListEquipmentShiftsAsync([.. counts.Select(c => c.EquipmentId).Distinct()], cancellationToken))
            .ToDictionary(e => e.EquipmentId, e => new LineCalendar(e.Zone, e.ScheduleCode, e.Schedule));
        var siteCalendar = new LineCalendar(zone, null, null);

        var totals = ShiftProduction.Total(
            counts.Select(c => new ProductionCount(c.EquipmentId, c.JobId, c.UomId, c.OccurredAt, c.CountKind == CountKind.Defective, c.Quantity)),
            id => calendars.GetValueOrDefault(id, siteCalendar), from, to);

        var lines = counts.DistinctBy(c => c.EquipmentId).ToDictionary(c => c.EquipmentId);
        var jobs = counts.DistinctBy(c => c.JobId).ToDictionary(c => c.JobId);
        var units = counts.DistinctBy(c => c.UomId).ToDictionary(c => c.UomId, c => c.UomCode);
        var rows = totals.Select(t => new ShiftProductionRowDto
        {
            ProductionDate = t.ProductionDate,
            ScheduleCode = t.ScheduleCode,
            ShiftCode = t.Shift?.Code,
            ShiftName = t.Shift?.Name,
            Crew = t.Shift?.Crew,
            ShiftStart = t.Shift is { } s ? TimeZoneInfo.ConvertTime(s.Start, zone) : null,
            ShiftEnd = t.Shift is { } e ? TimeZoneInfo.ConvertTime(e.End, zone) : null,
            EquipmentId = t.EquipmentId,
            LineCode = lines[t.EquipmentId].LineCode,
            JobId = t.JobId,
            JobNumber = jobs[t.JobId].JobNumber,
            WorkOrderNumber = jobs[t.JobId].WorkOrderNumber,
            MaterialCode = jobs[t.JobId].MaterialCode,
            MaterialName = jobs[t.JobId].MaterialName,
            UomCode = units[t.UomId],
            GoodQty = t.GoodQty,
            ScrapQty = t.ScrapQty,
            Reports = t.Reports,
        }).ToList();

        // Each job against its runs: equal when all its reports fall in these days.
        var reported = rows.GroupBy(r => r.JobId).ToList();
        var runs = reported.Count == 0 ? [] : (await store.ListRunTotalsAsync([.. reported.Select(g => g.Key)], cancellationToken)).ToDictionary(r => r.JobId);
        var checks = reported.Select(g =>
        {
            var run = runs.GetValueOrDefault(g.Key);
            return new ShiftProductionJobDto
            {
                JobId = g.Key,
                JobNumber = g.First().JobNumber,
                UomCode = string.Join(", ", g.Select(r => r.UomCode).Distinct()),
                GoodQty = g.Sum(r => r.GoodQty),
                ScrapQty = g.Sum(r => r.ScrapQty),
                RunGoodQty = run?.GoodQty ?? 0,
                RunScrapQty = run?.ScrapQty ?? 0,
                MatchesRuns = run is not null && g.Sum(r => r.GoodQty) == run.GoodQty && g.Sum(r => r.ScrapQty) == run.ScrapQty,
            };
        }).OrderBy(j => j.JobNumber, StringComparer.Ordinal).ToList();

        return new ProductionByShiftDto
        {
            SiteId = query.SiteId,
            From = from,
            To = to,
            TimeZone = timeZone,
            Rows = [.. rows.OrderBy(r => r.ProductionDate).ThenBy(r => r.ShiftStart ?? DateTimeOffset.MaxValue).ThenBy(r => r.ShiftCode, StringComparer.Ordinal)
                .ThenBy(r => r.LineCode, StringComparer.Ordinal).ThenBy(r => r.JobNumber, StringComparer.Ordinal)],
            Jobs = checks,
        };
    }

    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
