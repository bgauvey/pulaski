using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Application.Units;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Overview;

namespace Pulaski.Application.Overview;

/// <summary>A line of a site: active equipment that can run jobs.</summary>
public sealed record OverviewLineRecord(Guid Id, string Code);

/// <summary>Read side for the Overview (D-070) beyond the machine history the OEE report reads.</summary>
public interface IOverviewReadStore
{
    /// <summary>The site's active lines, by code.</summary>
    Task<IReadOnlyList<OverviewLineRecord>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>Lots at the site with stock that are on hold, and in quarantine.</summary>
    Task<(int OnHold, int Quarantine)> CountHeldLotsAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>The Overview's live figures for a site (D-070).</summary>
public sealed record GetOverviewQuery(Guid SiteId) : IQuery<OverviewDto>;

/// <summary>
/// Each line's live state (as on the Machines board), and its current shift so far: output reported (D-035, never machine
/// counts), down time and its reasons, uncoded stops and what its time was used for (D-052). Nothing is stored.
/// </summary>
internal sealed class GetOverviewHandler(
    IOverviewReadStore store, IOeeReadStore oee, IUnitReadStore units, IQueryHandler<GetMachineStatusQuery, IReadOnlyList<MachineStatusDto>> machines,
    TimeProvider time) : IQueryHandler<GetOverviewQuery, OverviewDto>
{
    public async Task<OverviewDto> HandleAsync(GetOverviewQuery query, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(await oee.GetSiteTimeZoneAsync(query.SiteId, cancellationToken) ?? "UTC");
        var lines = await store.ListLinesAsync(query.SiteId, cancellationToken);
        var lineIds = lines.Select(l => l.Id).ToList();
        var calendars = (await oee.ListEquipmentShiftsAsync(lineIds, cancellationToken)).ToDictionary(c => c.EquipmentId);
        var scopes = lines.ToDictionary(l => l.Id, l => OverviewFigures.Scope(calendars.GetValueOrDefault(l.Id)?.Schedule, calendars.GetValueOrDefault(l.Id)?.Zone ?? zone, now));
        var window = OverviewFigures.Window(scopes.Values.Where(s => s.Shift is not null).Select(s => s.Shift!), now);

        // History from the strip's start or the earliest shift's, whichever is first, to now.
        var earliest = scopes.Count == 0 ? window.Start : scopes.Values.Min(s => s.From);
        var start = earliest < window.Start ? earliest : window.Start;
        var tracked = await oee.ListEquipmentAsync(query.SiteId, cancellationToken);
        var uses = await EquipmentUses.LoadAsync(oee, query.SiteId, tracked, lineIds.Where(id => tracked.Any(t => t.Id == id)), start, now, cancellationToken);

        var runs = (await oee.ListRunsAsync(lineIds, earliest, now, cancellationToken)).ToDictionary(r => r.RunId);
        var counts = (await oee.ListCountsAsync(lineIds, earliest, now, cancellationToken)).Where(c => runs.ContainsKey(c.RunId)).ToList();
        var unitCodes = counts.Count == 0 ? [] : (await units.GetUnitsAsync(cancellationToken)).ToDictionary(u => u.Id, u => u.Code);
        var status = (await machines.HandleAsync(new GetMachineStatusQuery(query.SiteId), cancellationToken)).ToDictionary(m => m.EquipmentId);

        var down = lines.ToDictionary(l => l.Id, l => OverviewFigures.Down(uses.Of(l.Id), scopes[l.Id].From));
        var reasonIds = down.Values.SelectMany(d => d).Where(r => r.ReasonCodeId is not null).Select(r => r.ReasonCodeId!.Value).Distinct().ToList();
        var reasons = reasonIds.Count == 0 ? [] : (await oee.ListReasonsAsync(reasonIds, cancellationToken)).ToDictionary(r => r.Id);
        var (onHold, quarantine) = await store.CountHeldLotsAsync(query.SiteId, cancellationToken);

        return new OverviewDto
        {
            SiteId = query.SiteId,
            AsOf = now,
            WindowStart = window.Start,
            WindowEnd = window.End,
            WindowIsShift = window.IsShift,
            LotsOnHold = onHold,
            LotsInQuarantine = quarantine,
            Lines = [.. lines.Select(line =>
            {
                var scope = scopes[line.Id];
                var tracks = uses.Tracks(line.Id);
                var live = tracks ? status.GetValueOrDefault(line.Id) : null;
                var output = counts
                    .Where(c => runs[c.RunId].EquipmentId == line.Id && c.OccurredAt >= scope.From)
                    .GroupBy(c => c.UomId ?? runs[c.RunId].JobUomId)
                    .Where(g => g.Key is { } uom && unitCodes.ContainsKey(uom))
                    .Select(g => new OverviewOutputDto
                    {
                        UomCode = unitCodes[g.Key!.Value],
                        GoodQty = Math.Round(g.Sum(c => c.Processed - c.Defective), 3),
                        ScrapQty = Math.Round(g.Sum(c => c.Defective), 3),
                    })
                    .OrderBy(o => o.UomCode, StringComparer.Ordinal);
                return new OverviewLineDto
                {
                    EquipmentId = line.Id,
                    Code = line.Code,
                    TracksState = tracks,
                    From = TimeZoneInfo.ConvertTime(scope.From, zone),
                    Shift = scope.Shift is { } shift
                        ? new OverviewShiftDto
                        {
                            ProductionDate = shift.ProductionDate,
                            ShiftCode = shift.Code,
                            ShiftName = shift.Name,
                            Crew = shift.Crew,
                            Start = TimeZoneInfo.ConvertTime(shift.Start, zone),
                            End = TimeZoneInfo.ConvertTime(shift.End, zone),
                        }
                        : null,
                    Category = tracks ? live?.Category ?? "no_signal" : null,
                    PackMlName = live?.PackMlName,
                    StateSince = live?.StateSince,
                    StopText = live?.OpenStop?.ReasonName ?? live?.StopCodeText,
                    EdgeQuiet = live?.EdgeQuiet ?? false,
                    Output = [.. output],
                    DownMinutes = tracks ? Minutes(down[line.Id].Sum(r => r.Length.Ticks)) : null,
                    DownReasons = tracks
                        ? [.. down[line.Id].Select(r => new OverviewReasonDto
                        {
                            ReasonCode = r.ReasonCodeId is { } id && reasons.TryGetValue(id, out var found) ? found.Code : null,
                            Name = r.ReasonCodeId is { } named && reasons.TryGetValue(named, out var reason) ? reason.Name : r.NoSignal ? "No signal" : "Not coded",
                            NoSignal = r.NoSignal,
                            Minutes = Minutes(r.Length.Ticks),
                        })]
                        : [],
                    UncodedStops = tracks ? OverviewFigures.Uncoded(uses.StopsOf(line.Id), scope.From, now) : null,
                    States = tracks ? OverviewFigures.Lane(uses.Of(line.Id), scope.From < window.Start ? scope.From : window.Start) : [],
                };
            })],
        };
    }

    private static decimal Minutes(long ticks) => Math.Round((decimal)ticks / TimeSpan.TicksPerMinute, 1);
}
