using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>
/// Utilization and OEE between two production days (D-052), for a piece of equipment and everything below it that tracks state,
/// or the whole site: by equipment, shift and product, and where each one's time went.
/// </summary>
public sealed record GetOeeReportQuery(Guid SiteId, DateOnly From, DateOnly To, Guid? EquipmentId) : IQuery<OeeReportDto>;

internal sealed class GetOeeReportHandler(IOeeReadStore store, IUnitReadStore units, TimeProvider time) : IQueryHandler<GetOeeReportQuery, OeeReportDto>
{
    public const int MaxDays = 31;
    private const int MaxDepth = 16;
    private const int MaxNotes = 10;

    public async Task<OeeReportDto> HandleAsync(GetOeeReportQuery query, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var (from, to) = query.From <= query.To ? (query.From, query.To) : (query.To, query.From);
        if (to.DayNumber - from.DayNumber >= MaxDays)
        {
            from = to.AddDays(-(MaxDays - 1));
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(await store.GetSiteTimeZoneAsync(query.SiteId, cancellationToken) ?? "UTC");
        var equipment = await store.ListEquipmentAsync(query.SiteId, cancellationToken);
        var byId = equipment.ToDictionary(e => e.Id);

        var shown = equipment;
        if (query.EquipmentId is { } chosen)
        {
            if (!byId.ContainsKey(chosen))
            {
                throw new NotFoundException($"Equipment {chosen} isn't equipment of this site that tracks state.");
            }

            var below = new HashSet<Guid> { chosen };
            foreach (var e in equipment)
            {
                // Tree order: a parent comes before what is below it.
                if (e.ParentId is { } parent && below.Contains(parent))
                {
                    below.Add(e.Id);
                }
            }

            shown = [.. equipment.Where(e => below.Contains(e.Id))];
        }

        // Shifts of the last day may run into the next; nothing after now.
        var start = Utilization.DayStart(from, zone);
        var end = Min(Utilization.DayStart(to.AddDays(2), zone), now);
        var shownIds = shown.Select(e => e.Id).ToList();
        var calendars = (await store.ListEquipmentShiftsAsync(shownIds, cancellationToken)).ToDictionary(c => c.EquipmentId);
        var uses = await EquipmentUses.LoadAsync(store, query.SiteId, equipment, shownIds, start, end, cancellationToken);
        var runs = await store.ListRunsAsync(shownIds, start, end, cancellationToken);
        var runsById = runs.ToDictionary(r => r.RunId);
        var runsOn = runs.ToLookup(r => r.EquipmentId);
        var counts = (await store.ListCountsAsync(shownIds, start, end, cancellationToken))
            .Where(c => runsById.ContainsKey(c.RunId))
            .ToLookup(c => runsById[c.RunId].EquipmentId);

        var materials = runs.Where(r => r.MaterialId is not null).Select(r => r.MaterialId!.Value).Distinct().ToList();
        var unitList = materials.Count == 0 ? [] : await units.GetUnitsAsync(cancellationToken);
        var conversions = materials.Count == 0 ? [] : await units.GetConversionsAsync(materials, cancellationToken);
        var converters = materials.ToDictionary(m => m, m => UnitConversions.For(m, unitList, conversions));

        // The ideal rate in a unit of the run's material; null without a capability, or when its unit doesn't convert.
        decimal? RateIn(OeeRunRecord run, Guid? uom) =>
            run is { IdealRatePerHour: { } rate, RateUomId: { } rateUom, MaterialId: { } material } && uom is { } unit && converters[material].Factor(rateUom, unit) is { } factor
                ? rate * factor
                : null;
        var hasRate = runs.ToDictionary(r => r.RunId, r => RateIn(r, r.JobUomId) is not null);

        // What a run makes, for the product rows: a material in the job's unit; nothing for planned downtime or a job without one.
        static (Guid Material, Guid Uom)? ProductOf(OeeRunRecord run) =>
            run is { MaterialId: { } material, JobUomId: { } uom, CountsAsPlannedDowntime: false } ? (material, uom) : null;

        var shownSet = shownIds.ToHashSet();
        var equipmentRows = new List<OeeEquipmentRowDto>();
        var shiftRows = new List<(DateTimeOffset Start, int Order, OeeShiftRowDto Row)>();
        var products = new Dictionary<(Guid Material, Guid Uom), (OeeTally Tally, HashSet<Guid> Runs, List<string> Codes)>();
        var timeUses = new List<(int Order, Guid EquipmentId, string Code, string Use, Guid? Reason, bool NoSignal, TimeSpan Length, int Stops, TimeSpan Scheduled)>();
        var reported = new HashSet<Guid>();

        (OeeTally Tally, HashSet<Guid> Runs, List<string> Codes) Product((Guid Material, Guid Uom) key, OeeRunRecord run, string equipmentCode)
        {
            if (!products.TryGetValue(key, out var product))
            {
                products[key] = product = (new OeeTally(), [], []);
            }

            product.Runs.Add(run.RunId);
            if (!product.Codes.Contains(equipmentCode))
            {
                product.Codes.Add(equipmentCode);
            }

            return product;
        }

        for (var order = 0; order < shown.Count; order++)
        {
            var e = shown[order];
            var calendar = calendars.GetValueOrDefault(e.Id);
            var windows = Utilization.Windows(calendar?.Schedule, calendar?.Zone ?? zone, from, to, end);
            var slices = Utilization.Slice(uses.Of(e.Id), windows, [.. runsOn[e.Id].Select(r => new RunWindow(r.RunId, r.StartedAt, r.EndedAt))], now)
                .Select(s => s.RunId is { } r && runsById[r].CountsAsPlannedDowntime ? Utilization.InPlannedJob(s) : s);

            var total = new OeeTally();
            var perWindow = windows.Select(_ => new OeeTally()).ToArray();
            var byUse = new Dictionary<(string Use, Guid? Reason, bool NoSignal), (TimeSpan Length, int Stops)>();
            foreach (var slice in slices)
            {
                var rated = slice.RunId is { } runId && hasRate[runId];
                total.Add(slice, rated);
                perWindow[slice.Window].Add(slice, rated);
                if (slice.RunId is { } id)
                {
                    reported.Add(id);
                    if (ProductOf(runsById[id]) is { } product)
                    {
                        Product(product, runsById[id], e.Code).Tally.Add(slice, rated);
                    }
                }

                var span = slice.Span;
                var key = (UseName(span), span.ReasonCodeId, span.Use == TimeUse.Down && span.ReasonCodeId is null && span.NoSignal);
                var (length, n) = byUse.GetValueOrDefault(key);
                byUse[key] = (length + span.Length, n + (span.StartsStop ? 1 : 0));
            }

            foreach (var count in counts[e.Id])
            {
                var run = runsById[count.RunId];
                var uom = count.UomId ?? run.JobUomId;
                var rate = RateIn(run, uom);
                total.AddCount(count.Processed, count.Defective, rate);
                if (Utilization.WindowOf(windows, count.OccurredAt) is var w and >= 0)
                {
                    perWindow[w].AddCount(count.Processed, count.Defective, rate);
                }

                // Products add up in the jobs' unit.
                reported.Add(run.RunId);
                if (ProductOf(run) is { } product && uom is { } unit && converters[product.Material].Factor(unit, product.Uom) is { } factor)
                {
                    Product(product, run, e.Code).Tally.AddCount(count.Processed * factor, count.Defective * factor, RateIn(run, product.Uom));
                }
            }

            var depth = 0;
            for (var parent = e.ParentId; parent is { } p && shownSet.Contains(p) && depth < MaxDepth; parent = byId[p].ParentId)
            {
                depth++;
            }

            equipmentRows.Add(new OeeEquipmentRowDto
            {
                EquipmentId = e.Id,
                Code = e.Code,
                Name = e.Name,
                ParentId = e.ParentId,
                Depth = depth,
                StateDerivation = e.StateDerivation,
                CanRunJob = e.CanRunJob,
                Figures = Figures(total),
            });

            for (var w = 0; w < windows.Count; w++)
            {
                if (perWindow[w].Scheduled <= TimeSpan.Zero)
                {
                    continue;
                }

                var window = windows[w];
                shiftRows.Add((window.Start, order, new OeeShiftRowDto
                {
                    ProductionDate = window.ProductionDate,
                    ShiftCode = window.ShiftCode,
                    ShiftName = window.ShiftName,
                    CrewCode = window.Crew,
                    Start = TimeZoneInfo.ConvertTime(window.Start, zone),
                    End = TimeZoneInfo.ConvertTime(window.End, zone),
                    EquipmentId = e.Id,
                    EquipmentCode = e.Code,
                    CanRunJob = e.CanRunJob,
                    Figures = Figures(perWindow[w]),
                }));
            }

            foreach (var ((use, reason, noSignal), (length, n)) in byUse)
            {
                timeUses.Add((order, e.Id, e.Code, use, reason, noSignal, length, n, total.Scheduled));
            }
        }

        var reasons = (await store.ListReasonsAsync([.. timeUses.Where(u => u.Reason is not null).Select(u => u.Reason!.Value).Distinct()], cancellationToken))
            .ToDictionary(r => r.Id);
        var noRate = runs.Where(r => reported.Contains(r.RunId) && ProductOf(r) is not null && !hasRate[r.RunId]).ToList();
        var notes = noRate.Take(MaxNotes)
            .Select(r => $"{r.JobNumber} on {byId[r.EquipmentId].Code} had no ideal rate for {r.MaterialCode} in its unit when it started, so its output isn't in performance or quality.")
            .ToList();
        if (noRate.Count > MaxNotes)
        {
            notes.Add($"…and {noRate.Count - MaxNotes} more runs without an ideal rate.");
        }

        return new OeeReportDto
        {
            From = from,
            To = to,
            Equipment = equipmentRows,
            Shifts = [.. shiftRows.OrderBy(s => s.Start).ThenBy(s => s.Order).Select(s => s.Row)],
            Products = [.. products.Select(p =>
            {
                var run = runs.First(r => r.MaterialId == p.Key.Material);
                return new OeeProductRowDto
                {
                    MaterialId = p.Key.Material,
                    MaterialCode = run.MaterialCode ?? "",
                    MaterialName = run.MaterialName ?? "",
                    UomCode = converters[p.Key.Material].Code(p.Key.Uom),
                    GoodQty = Math.Round(p.Value.Tally.Good, 3),
                    ScrapQty = Math.Round(p.Value.Tally.Scrap, 3),
                    Runs = p.Value.Runs.Count,
                    EquipmentCodes = p.Value.Codes,
                    Figures = Figures(p.Value.Tally),
                };
            }).OrderBy(p => p.MaterialCode, StringComparer.Ordinal).ThenBy(p => p.UomCode, StringComparer.Ordinal)],
            Utilization = [.. timeUses.OrderBy(u => u.Order).ThenByDescending(u => u.Length).Select(u => new UtilizationRowDto
            {
                EquipmentId = u.EquipmentId,
                EquipmentCode = u.Code,
                Use = u.Use,
                ReasonCodeId = u.Reason,
                ReasonCode = u.Reason is { } r && reasons.TryGetValue(r, out var found) ? found.Code : null,
                ReasonName = u.Reason is { } r2 && reasons.TryGetValue(r2, out var named) ? named.Name : null,
                NoSignal = u.NoSignal,
                Hours = Hours(u.Length),
                SharePct = u.Scheduled > TimeSpan.Zero ? Math.Round(OeeTally.Hours(u.Length) / OeeTally.Hours(u.Scheduled) * 100m, 1) : 0,
                Stops = u.Stops,
            })],
            Notes = notes,
        };
    }

    private static string UseName(UseSpan span) => span.Use switch
    {
        TimeUse.Run when span.IsMicroStop => "micro_stop",
        TimeUse.Run => "run",
        TimeUse.Down => "down",
        TimeUse.Planned => "planned",
        _ => "non_production",
    };

    private static OeeFiguresDto Figures(OeeTally t) => new()
    {
        ScheduledHours = Hours(t.Scheduled),
        NonProductionHours = Hours(t.NonProduction),
        PlannedStopHours = Hours(t.PlannedStop),
        PlannedHours = Hours(t.Planned),
        RunHours = Hours(t.Run),
        DownHours = Hours(t.Down),
        UncodedHours = Hours(t.Uncoded),
        NoSignalHours = Hours(t.NoSignal),
        RunWithoutJobHours = Hours(t.RunWithoutJob),
        Stops = t.Stops,
        MicroStops = t.MicroStops,
        AvailabilityPct = Pct(t.Availability),
        PerformancePct = Pct(t.Performance),
        QualityPct = Pct(t.Quality),
        OeePct = Pct(t.Oee),
        PlannedHoursForOee = OeeTally.Hours(t.Planned),
        RunHoursForAvailability = OeeTally.Hours(t.Run),
        RatedRunHoursForPerformance = OeeTally.Hours(t.RunWithRate),
        IdealHours = t.EarnedHours,
        GoodIdealHours = t.EarnedGoodHours,
    };

    private static decimal Hours(TimeSpan span) => Math.Round(OeeTally.Hours(span), 2);

    private static decimal? Pct(decimal? ratio) => ratio is { } r ? Math.Round(r * 100m, 1) : null;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
