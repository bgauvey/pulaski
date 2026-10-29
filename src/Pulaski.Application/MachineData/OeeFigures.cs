using Pulaski.Application.Units;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>Part of a window's time: one use, and one reason within it, in ticks.</summary>
public sealed record OeeUseFigures(string Use, Guid? ReasonCodeId, bool NoSignal, long Ticks, int Stops);

/// <summary>A material made in a window, in the jobs' unit, with the runs that made it.</summary>
public sealed record OeeProductFigures(Guid MaterialId, Guid UomId, OeeTallyParts Tally, IReadOnlyList<Guid> RunIds);

/// <summary>A run reported in a window whose output can't count in performance or quality: it had no ideal rate in its unit.</summary>
public sealed record OeeUnratedRun(Guid RunId, string JobNumber, Guid MaterialId);

/// <summary>
/// One piece of equipment's figures for one working window (#66): what the OEE report adds up, and what a daily rollup keeps.
/// <see cref="Start"/> is null for output reported on a day with no working window before it, which counts in the day but no shift.
/// Windows add up exactly, so a period's report is the sum of its days.
/// </summary>
public sealed record OeeWindowFigures(
    Guid EquipmentId, DateOnly ProductionDate, string? ShiftCode, string? ShiftName, string? Crew, DateTimeOffset? Start, DateTimeOffset? End,
    OeeTallyParts Tally, IReadOnlyList<OeeUseFigures> Uses, IReadOnlyList<OeeProductFigures> Products, IReadOnlyList<OeeUnratedRun> Unrated);

/// <summary>
/// Works out each piece of equipment's windows of production days <c>from</c> to <c>to</c> (D-052) from the machine history, the runs and
/// the production reported. A count belongs to the window it was made in, else the last one that started before it on its own site-local
/// day or the day before; with neither, to its day outside every window. So every count belongs to exactly one production day.
/// </summary>
internal static class OeeFigurer
{
    public static async Task<IReadOnlyList<OeeWindowFigures>> FigureAsync(
        IOeeReadStore store, IUnitReadStore units, Guid siteId, TimeZoneInfo zone, IReadOnlyList<OeeEquipmentRecord> equipment,
        IReadOnlyList<OeeEquipmentRecord> shown, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Shifts of the last day may run into the next, and output reported after it may still be its; nothing after now.
        var start = Utilization.DayStart(from, zone);
        var end = Min(Utilization.DayStart(to.AddDays(2), zone), now);
        if (shown.Count == 0 || start >= end)
        {
            return [];
        }

        var shownIds = shown.Select(e => e.Id).ToList();
        var calendars = (await store.ListEquipmentShiftsAsync(shownIds, cancellationToken)).ToDictionary(c => c.EquipmentId);
        var uses = await EquipmentUses.LoadAsync(store, siteId, equipment, shownIds, start, end, cancellationToken);
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

        var result = new List<OeeWindowFigures>();
        foreach (var e in shown)
        {
            var calendar = calendars.GetValueOrDefault(e.Id);
            var calendarZone = calendar?.Zone ?? zone;
            var around = Utilization.Windows(calendar?.Schedule, calendarZone, from.AddDays(-1), to.AddDays(1), end);
            var windows = around.Where(w => w.ProductionDate >= from && w.ProductionDate <= to).ToList();
            var pieces = windows.Select(w => new Piece(w.ProductionDate, w)).ToList();
            var outside = new SortedDictionary<DateOnly, Piece>();

            var slices = Utilization.Slice(uses.Of(e.Id), windows, [.. runsOn[e.Id].Select(r => new RunWindow(r.RunId, r.StartedAt, r.EndedAt))], now)
                .Select(s => s.RunId is { } r && runsById[r].CountsAsPlannedDowntime ? Utilization.InPlannedJob(s) : s);
            foreach (var slice in slices)
            {
                var piece = pieces[slice.Window];
                var rated = slice.RunId is { } runId && hasRate[runId];
                piece.Tally.Add(slice, rated);
                if (slice.RunId is { } id)
                {
                    var run = runsById[id];
                    piece.Reported(run, hasRate[id]);
                    if (ProductOf(run) is { } product)
                    {
                        piece.Product(product, id).Add(slice, rated);
                    }
                }

                piece.Use(slice.Span);
            }

            foreach (var count in counts[e.Id])
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(count.OccurredAt, calendarZone).DateTime);
                var candidates = around.Where(w => w.ProductionDate >= day.AddDays(-1) && w.ProductionDate <= day).ToList();
                var owner = Utilization.WindowOf(candidates, count.OccurredAt);
                Piece? piece = null;
                if (owner >= 0)
                {
                    var w = windows.IndexOf(candidates[owner]);
                    piece = w >= 0 ? pieces[w] : null;
                }
                else if (day >= from && day <= to)
                {
                    if (!outside.TryGetValue(day, out piece))
                    {
                        outside[day] = piece = new Piece(day, null);
                    }
                }

                if (piece is null)
                {
                    continue;
                }

                var run = runsById[count.RunId];
                var uom = count.UomId ?? run.JobUomId;
                piece.Tally.AddCount(count.Processed, count.Defective, RateIn(run, uom));
                piece.Reported(run, hasRate[run.RunId]);

                // Products add up in the jobs' unit.
                if (ProductOf(run) is { } product && uom is { } unit && converters[product.Material].Factor(unit, product.Uom) is { } factor)
                {
                    piece.Product(product, run.RunId).AddCount(count.Processed * factor, count.Defective * factor, RateIn(run, product.Uom));
                }
            }

            result.AddRange(pieces.Concat(outside.Values).Select(p => p.Figures(e.Id)));
        }

        return result;
    }

    // What a run makes, for the product rows: a material in the job's unit; nothing for planned downtime or a job without one.
    private static (Guid Material, Guid Uom)? ProductOf(OeeRunRecord run) =>
        run is { MaterialId: { } material, JobUomId: { } uom, CountsAsPlannedDowntime: false } ? (material, uom) : null;

    public static string UseName(UseSpan span) => span.Use switch
    {
        TimeUse.Run when span.IsMicroStop => "micro_stop",
        TimeUse.Run => "run",
        TimeUse.Down => "down",
        TimeUse.Planned => "planned",
        _ => "non_production",
    };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    // One window being added up.
    private sealed class Piece(DateOnly day, WorkWindow? window)
    {
        private readonly Dictionary<(string Use, Guid? Reason, bool NoSignal), (long Ticks, int Stops)> _uses = [];
        private readonly Dictionary<(Guid Material, Guid Uom), (OeeTally Tally, List<Guid> Runs)> _products = [];
        private readonly Dictionary<Guid, OeeUnratedRun> _unrated = [];

        public OeeTally Tally { get; } = new();

        public void Use(UseSpan span)
        {
            var key = (UseName(span), span.ReasonCodeId, span.Use == TimeUse.Down && span.ReasonCodeId is null && span.NoSignal);
            var (ticks, stops) = _uses.GetValueOrDefault(key);
            _uses[key] = (ticks + span.Length.Ticks, stops + (span.StartsStop ? 1 : 0));
        }

        public OeeTally Product((Guid Material, Guid Uom) key, Guid runId)
        {
            if (!_products.TryGetValue(key, out var product))
            {
                _products[key] = product = (new OeeTally(), []);
            }

            if (!product.Runs.Contains(runId))
            {
                product.Runs.Add(runId);
            }

            return product.Tally;
        }

        public void Reported(OeeRunRecord run, bool hasRate)
        {
            if (!hasRate && ProductOf(run) is { } product)
            {
                _unrated.TryAdd(run.RunId, new OeeUnratedRun(run.RunId, run.JobNumber, product.Material));
            }
        }

        public OeeWindowFigures Figures(Guid equipmentId) => new(
            equipmentId, day, window?.ShiftCode, window?.ShiftName, window?.Crew, window?.Start, window?.End, Tally.Parts,
            [.. _uses.Select(u => new OeeUseFigures(u.Key.Use, u.Key.Reason, u.Key.NoSignal, u.Value.Ticks, u.Value.Stops))],
            [.. _products.Select(p => new OeeProductFigures(p.Key.Material, p.Key.Uom, p.Value.Tally.Parts, p.Value.Runs))],
            [.. _unrated.Values]);
    }
}
