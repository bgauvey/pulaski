using Pulaski.Application.Abstractions;
using Pulaski.Application.Andon;
using Pulaski.Application.MachineData;
using Pulaski.Application.Overview;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Andon;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Overview;

namespace Pulaski.Application.Execution;

/// <summary>
/// A line display board (#120): the lines at and below <paramref name="ScopeEquipmentId"/> in the site, each with its state, running and next
/// job, shift output and OEE so far. Built from the Overview (D-070), the floor board (D-035) and the OEE report (D-052), so the board can
/// never disagree with them. <paramref name="LineCode"/> narrows it to one line.
/// </summary>
public sealed record GetLineDisplayQuery(Guid SiteId, Guid ScopeEquipmentId, string Title, string? LineCode = null) : IQuery<LineDisplayDto>;

internal sealed class GetLineDisplayHandler(
    IQueryHandler<GetOverviewQuery, OverviewDto> overview,
    IQueryHandler<GetLineBoardQuery, IReadOnlyList<LineBoardRowDto>> board,
    IQueryHandler<GetOeeReportQuery, OeeReportDto> oee,
    IQueryHandler<ListAndonCallsQuery, IReadOnlyList<AndonCallDto>> andon,
    IWorkOrderReadStore scopes,
    TimeProvider timeProvider) : IQueryHandler<GetLineDisplayQuery, LineDisplayDto>
{
    public async Task<LineDisplayDto> HandleAsync(GetLineDisplayQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var inScope = (await scopes.GetScopeAsync(query.ScopeEquipmentId, cancellationToken)).Select(e => e.Id).ToHashSet();
        var live = await overview.HandleAsync(new GetOverviewQuery(query.SiteId), cancellationToken);
        var jobs = (await board.HandleAsync(new GetLineBoardQuery(query.SiteId), cancellationToken)).ToDictionary(r => r.LineId);

        // The current shift's OEE: the report's shift rows around today, whichever covers now (the days either side cover any time zone).
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var report = await oee.HandleAsync(new GetOeeReportQuery(query.SiteId, today.AddDays(-1), today.AddDays(1), null), cancellationToken);
        var shiftOee = report.Shifts.Where(s => s.Start <= now && now < s.End).GroupBy(s => s.EquipmentId).ToDictionary(g => g.Key, g => g.First());

        // Calls for help not yet resolved (#158), the longest waiting first.
        var calls = (await andon.HandleAsync(new ListAndonCallsQuery(query.SiteId, true), cancellationToken))
            .OrderBy(c => c.RaisedAt).ToLookup(c => c.LineId);

        var lines = live.Lines
            .Where(l => inScope.Contains(l.EquipmentId) && jobs.TryGetValue(l.EquipmentId, out var row) && !row.IsGroup)
            .Where(l => query.LineCode is null || string.Equals(l.Code, query.LineCode, StringComparison.OrdinalIgnoreCase))
            .Select(l => Line(l, jobs[l.EquipmentId], shiftOee.GetValueOrDefault(l.EquipmentId)) with { Calls = [.. calls[l.EquipmentId]] })
            .ToList();

        return new LineDisplayDto { Title = query.Title, AsOf = now, Lines = lines };
    }

    private static LineDisplayLineDto Line(OverviewLineDto line, LineBoardRowDto jobs, OeeShiftRowDto? oee) => new()
    {
        LineId = line.EquipmentId,
        Code = line.Code,
        Name = jobs.LineName,
        Status = LineDisplayStatus.Of(line.TracksState, line.Category, jobs.Running is not null),
        StateName = line.PackMlName,
        StateSince = line.StateSince,
        StopText = line.StopText,
        ShiftName = line.Shift is { } shift ? shift.ShiftName ?? shift.ShiftCode : null,
        ShiftEnd = line.Shift?.End,
        Running = jobs.Running is { } r
            ? new LineDisplayJobDto { JobNumber = r.JobNumber, MaterialCode = r.MaterialCode, PlannedQty = r.PlannedQty, GoodQty = r.GoodQty, UomCode = r.UomCode }
            : null,
        Next = jobs.Next.Count > 0 && jobs.Next[0] is { } n
            ? new LineDisplayJobDto { JobNumber = n.JobNumber, MaterialCode = n.MaterialCode, PlannedQty = n.PlannedQty, GoodQty = n.GoodQty, UomCode = n.UomCode }
            : null,
        Output = [.. line.Output.Select(o => new LineDisplayOutputDto(o.UomCode, o.GoodQty, o.ScrapQty))],
        DownMinutes = line.DownMinutes,
        UncodedStops = line.UncodedStops,
        OeePct = oee?.Figures.OeePct,
        AvailabilityPct = oee?.Figures.AvailabilityPct,
        PerformancePct = oee?.Figures.PerformancePct,
        QualityPct = oee?.Figures.QualityPct,
        OeeTargetPct = oee?.Target?.OeePct,
    };
}

/// <summary>What a board says of a line at a glance (#120). Pure.</summary>
public static class LineDisplayStatus
{
    /// <param name="tracksState">Whether the line reports machine state.</param>
    /// <param name="category">Its state's OEE category now (as on the Machines board), when it reports one.</param>
    /// <param name="hasJob">Whether a job is running on it.</param>
    public static string Of(bool tracksState, string? category, bool hasJob)
    {
        if (!tracksState)
        {
            return hasJob ? "running" : "idle";
        }

        return category switch
        {
            "producing" => "running",
            "no_signal" => "no_signal",
            "stopped_faulted" or "held_internal" => "down",
            "waiting_external" => "waiting",
            _ => hasJob ? "waiting" : "idle",
        };
    }
}
