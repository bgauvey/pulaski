using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>
/// Utilization and OEE between two production days (D-052), for a piece of equipment and everything below it that tracks state,
/// or the whole site: by equipment, shift and product, and where each one's time went. With <c>ShiftCode</c>, only the windows of that
/// shift (#21): their time, stops and the output reported in them; equipment without shifts then has none. Up to 31 days are worked out
/// from the history; a longer period, up to 366 days, comes from the daily rollups (#66), with the days not rolled yet worked out from
/// the history (at most 31 of them, the newest), and is shown by day rather than by shift. <c>FromRollups</c> uses rollups for any period.
/// </summary>
public sealed record GetOeeReportQuery(Guid SiteId, DateOnly From, DateOnly To, Guid? EquipmentId, string? ShiftCode = null, bool FromRollups = false)
    : IQuery<OeeReportDto>;

internal sealed class GetOeeReportHandler(IOeeReadStore store, IOeeRollupStore rollups, IUnitReadStore units, TimeProvider time)
    : IQueryHandler<GetOeeReportQuery, OeeReportDto>
{
    public const int MaxDays = 31;
    public const int MaxRollupDays = 366;
    private const int MaxDepth = 16;
    private const int MaxNotes = 10;

    public async Task<OeeReportDto> HandleAsync(GetOeeReportQuery query, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var (from, to) = query.From <= query.To ? (query.From, query.To) : (query.To, query.From);
        var fromRollups = query.FromRollups || to.DayNumber - from.DayNumber >= MaxDays;
        var longest = fromRollups ? MaxRollupDays : MaxDays;
        if (to.DayNumber - from.DayNumber >= longest)
        {
            from = to.AddDays(-(longest - 1));
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

        var shownIds = shown.Select(e => e.Id).ToList();
        var notes = new List<string>();
        IReadOnlyList<OeeWindowFigures> figures;
        IReadOnlyList<DateOnly> leftOut = [];
        if (!fromRollups)
        {
            figures = await OeeFigurer.FigureAsync(store, units, query.SiteId, zone, equipment, shown, from, to, now, cancellationToken);
        }
        else
        {
            // Rolled days come from their rollups; the rest (today and yesterday, and any being refigured) from the history, newest first.
            var current = await rollups.ListCurrentDaysAsync(query.SiteId, from, to, cancellationToken);
            var pending = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(d => to.AddDays(-d)).Where(d => !current.Contains(d)).ToList();
            var figured = pending.Take(MaxDays).ToHashSet();
            leftOut = [.. pending.Skip(MaxDays).Order()];

            var all = new List<OeeWindowFigures>(await rollups.ListFiguresAsync(shownIds, [.. current], cancellationToken));
            foreach (var (first, last) in Ranges(figured))
            {
                all.AddRange(await OeeFigurer.FigureAsync(store, units, query.SiteId, zone, equipment, shown, first, last, now, cancellationToken));
            }

            figures = all;
            if (leftOut.Count > 0)
            {
                notes.Add($"{leftOut.Count} days aren't included because their rollups are being worked out again: {DayRanges(leftOut)}. Try again shortly.");
            }
        }

        // Equipment rows are measured against the targets of the period's last day, or today's when that is still to come.
        var targets = await store.ListTargetsAsync(query.SiteId, cancellationToken);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var report = await AssembleAsync(shown, byId, figures, query.ShiftCode, fromRollups, zone, targets, to < today ? to : today, cancellationToken);
        return report with
        {
            From = from,
            To = to,
            ByDay = fromRollups,
            FromRollups = fromRollups,
            LeftOutDays = leftOut,
            Notes = [.. notes, .. report.Notes],
        };
    }

    /// <summary>Adds up the windows into the report's tables: by equipment, by shift (or by day), by product, and where the time went.</summary>
    private async Task<OeeReportDto> AssembleAsync(
        IReadOnlyList<OeeEquipmentRecord> shown, Dictionary<Guid, OeeEquipmentRecord> byId, IReadOnlyList<OeeWindowFigures> figures, string? shiftCode,
        bool byDay, TimeZoneInfo zone, IReadOnlyList<OeeTarget> targets, DateOnly targetDay, CancellationToken cancellationToken)
    {
        OeeTargetFiguresDto? TargetOf(Guid equipmentId, DateOnly day) =>
            OeeTargets.InForce(targets, equipmentId, day) is { Values: var v } ? new(v.AvailabilityPct, v.PerformancePct, v.QualityPct, v.OeePct) : null;

        var shownSet = shown.Select(e => e.Id).ToHashSet();
        var kept = figures.Where(f => shiftCode is null || (f.Start is not null && f.ShiftCode == shiftCode)).ToLookup(f => f.EquipmentId);

        var equipmentRows = new List<OeeEquipmentRowDto>();
        var shiftRows = new List<(DateTimeOffset Start, int Order, OeeShiftRowDto Row)>();
        var products = new Dictionary<(Guid Material, Guid Uom), (OeeTally Tally, HashSet<Guid> Runs, List<string> Codes)>();
        var timeUses = new List<(int Order, Guid EquipmentId, string Code, string Use, Guid? Reason, bool NoSignal, long Ticks, int Stops, TimeSpan Scheduled)>();
        var unrated = new Dictionary<Guid, (OeeUnratedRun Run, string EquipmentCode)>();

        for (var order = 0; order < shown.Count; order++)
        {
            var e = shown[order];
            var windows = kept[e.Id].OrderBy(f => f.ProductionDate).ThenBy(f => f.Start ?? DateTimeOffset.MaxValue).ToList();
            var total = new OeeTally();
            var byUse = new Dictionary<(string Use, Guid? Reason, bool NoSignal), (long Ticks, int Stops)>();
            foreach (var w in windows)
            {
                total.Add(OeeTally.From(w.Tally));
                foreach (var u in w.Uses)
                {
                    var (ticks, n) = byUse.GetValueOrDefault((u.Use, u.ReasonCodeId, u.NoSignal));
                    byUse[(u.Use, u.ReasonCodeId, u.NoSignal)] = (ticks + u.Ticks, n + u.Stops);
                }

                foreach (var p in w.Products)
                {
                    if (!products.TryGetValue((p.MaterialId, p.UomId), out var product))
                    {
                        products[(p.MaterialId, p.UomId)] = product = (new OeeTally(), [], []);
                    }

                    product.Tally.Add(OeeTally.From(p.Tally));
                    product.Runs.UnionWith(p.RunIds);
                    if (!product.Codes.Contains(e.Code))
                    {
                        product.Codes.Add(e.Code);
                    }
                }

                foreach (var run in w.Unrated)
                {
                    unrated.TryAdd(run.RunId, (run, e.Code));
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
                Target = TargetOf(e.Id, targetDay),
            });

            var timed = windows.Where(w => w.Start is not null);
            var rows = byDay
                ? timed.GroupBy(w => w.ProductionDate).Select(day => (
                    Day: day.Key, Code: shiftCode, Name: shiftCode is null ? null : day.First().ShiftName, Crew: (string?)null,
                    Start: day.Min(w => w.Start!.Value), End: day.Max(w => w.End!.Value), Parts: day.Select(w => w.Tally).ToList()))
                : timed.Select(w => (Day: w.ProductionDate, Code: w.ShiftCode, Name: w.ShiftName, w.Crew, Start: w.Start!.Value, End: w.End!.Value, Parts: new List<OeeTallyParts> { w.Tally }));
            foreach (var row in rows)
            {
                var tally = new OeeTally();
                row.Parts.ForEach(t => tally.Add(OeeTally.From(t)));
                if (tally.Scheduled <= TimeSpan.Zero)
                {
                    continue;
                }

                shiftRows.Add((row.Start, order, new OeeShiftRowDto
                {
                    ProductionDate = row.Day,
                    ShiftCode = row.Code,
                    ShiftName = row.Name,
                    CrewCode = row.Crew,
                    Start = TimeZoneInfo.ConvertTime(row.Start, zone),
                    End = TimeZoneInfo.ConvertTime(row.End, zone),
                    EquipmentId = e.Id,
                    EquipmentCode = e.Code,
                    CanRunJob = e.CanRunJob,
                    Figures = Figures(tally),
                    Target = TargetOf(e.Id, row.Day),
                }));
            }

            foreach (var ((use, reason, noSignal), (ticks, n)) in byUse)
            {
                timeUses.Add((order, e.Id, e.Code, use, reason, noSignal, ticks, n, total.Scheduled));
            }
        }

        var reasons = (await store.ListReasonsAsync([.. timeUses.Where(u => u.Reason is not null).Select(u => u.Reason!.Value).Distinct()], cancellationToken))
            .ToDictionary(r => r.Id);
        var materialIds = products.Keys.Select(k => k.Material).Concat(unrated.Values.Select(u => u.Run.MaterialId)).Distinct().ToList();
        var materials = materialIds.Count == 0 ? [] : (await store.ListMaterialsAsync(materialIds, cancellationToken)).ToDictionary(m => m.Id);
        var unitList = products.Count == 0 ? [] : await units.GetUnitsAsync(cancellationToken);

        var notes = unrated.Values.Take(MaxNotes)
            .Select(u => $"{u.Run.JobNumber} on {u.EquipmentCode} had no ideal rate for {materials.GetValueOrDefault(u.Run.MaterialId)?.Code} in its unit when it started, so its output isn't in performance or quality.")
            .ToList();
        if (unrated.Count > MaxNotes)
        {
            notes.Add($"…and {unrated.Count - MaxNotes} more runs without an ideal rate.");
        }

        return new OeeReportDto
        {
            Equipment = equipmentRows,
            Shifts = [.. shiftRows.OrderBy(s => s.Start).ThenBy(s => s.Order).Select(s => s.Row)],
            Products = [.. products.Select(p => new OeeProductRowDto
            {
                MaterialId = p.Key.Material,
                MaterialCode = materials.GetValueOrDefault(p.Key.Material)?.Code ?? "",
                MaterialName = materials.GetValueOrDefault(p.Key.Material)?.Name ?? "",
                UomCode = UnitConversions.For(p.Key.Material, unitList, []).Code(p.Key.Uom),
                GoodQty = Math.Round(p.Value.Tally.Good, 3),
                ScrapQty = Math.Round(p.Value.Tally.Scrap, 3),
                Runs = p.Value.Runs.Count,
                EquipmentCodes = p.Value.Codes,
                Figures = Figures(p.Value.Tally),
            }).OrderBy(p => p.MaterialCode, StringComparer.Ordinal).ThenBy(p => p.UomCode, StringComparer.Ordinal)],
            Utilization = [.. timeUses.OrderBy(u => u.Order).ThenByDescending(u => u.Ticks).Select(u => new UtilizationRowDto
            {
                EquipmentId = u.EquipmentId,
                EquipmentCode = u.Code,
                Use = u.Use,
                ReasonCodeId = u.Reason,
                ReasonCode = u.Reason is { } r && reasons.TryGetValue(r, out var found) ? found.Code : null,
                ReasonName = u.Reason is { } r2 && reasons.TryGetValue(r2, out var named) ? named.Name : null,
                ReasonGroups = u.Reason is { } r3 && reasons.TryGetValue(r3, out var grouped) ? grouped.Groups ?? [] : [],
                NoSignal = u.NoSignal,
                Hours = Hours(new TimeSpan(u.Ticks)),
                SharePct = u.Scheduled > TimeSpan.Zero ? Math.Round((decimal)u.Ticks / u.Scheduled.Ticks * 100m, 1) : 0,
                Stops = u.Stops,
            })],
            Notes = notes,
        };
    }

    // Consecutive days as (first, last) ranges, to work each stretch out in one go.
    private static IEnumerable<(DateOnly First, DateOnly Last)> Ranges(IEnumerable<DateOnly> days)
    {
        DateOnly? first = null, last = null;
        foreach (var day in days.Order())
        {
            if (last is { } l && day.DayNumber == l.DayNumber + 1)
            {
                last = day;
                continue;
            }

            if (first is { } f)
            {
                yield return (f, last!.Value);
            }

            (first, last) = (day, day);
        }

        if (first is { } start)
        {
            yield return (start, last!.Value);
        }
    }

    private static string DayRanges(IEnumerable<DateOnly> days) =>
        string.Join(", ", Ranges(days).Select(r => r.First == r.Last ? $"{r.First:yyyy-MM-dd}" : $"{r.First:yyyy-MM-dd} to {r.Last:yyyy-MM-dd}"));

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
}
