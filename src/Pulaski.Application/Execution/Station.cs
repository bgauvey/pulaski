using Pulaski.Contracts.Common;
using Pulaski.Application.Localization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Andon;
using Pulaski.Application.Inventory;
using Pulaski.Application.Labor;
using Pulaski.Application.MachineData;
using Pulaski.Application.Security;
using Pulaski.Domain.Security;
using Pulaski.Contracts.Andon;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Application.Execution;

/// <summary>
/// An operator station (#146): the terminal's line and who is signed on. The line is <paramref name="LineCode"/> when it is one of the
/// terminal's, else the terminal's first line offered, else its only line; with none of those the station offers its lines to choose from.
/// </summary>
public sealed record GetStationQuery(TerminalRecord Terminal, Guid? PersonId, string? LineCode) : IQuery<StationDto>;

internal sealed class GetStationHandler(
    IQueryHandler<GetLineDisplayQuery, LineDisplayDto> display,
    IQueryHandler<GetLineBoardQuery, IReadOnlyList<LineBoardRowDto>> board,
    IQueryHandler<GetTerminalStatusQuery, TerminalStatusDto> status,
    IQueryHandler<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>> reasons,
    IQueryHandler<GetJobExecutionQuery, JobExecutionDto> execution,
    IQueryHandler<GetMachineCountsQuery, MachineCountsDto> machine,
    IQueryHandler<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>> locations,
    IQueryHandler<ListStopsQuery, StopPageDto> stops,
    IQueryHandler<ListCodingReasonsQuery, IReadOnlyList<ReasonCodeDto>> codingReasons,
    IQueryHandler<ListAndonTypesQuery, IReadOnlyList<AndonTypeDto>> andonTypes,
    ISecurityReadStore security,
    TimeProvider timeProvider) : IQueryHandler<GetStationQuery, StationDto>
{
    public async Task<StationDto> HandleAsync(GetStationQuery query, CancellationToken cancellationToken)
    {
        var terminal = query.Terminal;
        var lines = (await display.HandleAsync(new GetLineDisplayQuery(terminal.SiteId, terminal.ScopeEquipmentId, terminal.Name), cancellationToken)).Lines;
        var line = StationLine.Choose(lines, query.LineCode, terminal.DefaultEquipmentId);
        var row = line is null
            ? null
            : (await board.HandleAsync(new GetLineBoardQuery(terminal.SiteId), cancellationToken)).FirstOrDefault(r => r.LineId == line.LineId);
        var person = query.PersonId is { } personId ? await status.HandleAsync(new GetTerminalStatusQuery(terminal, personId), cancellationToken) : null;

        // What they may do here is their user's role at the terminal's site (#150); without a user, only labor.
        var access = query.PersonId is { } id ? await security.GetPersonAccessAsync(id, timeProvider.GetUtcNow(), cancellationToken) : null;
        bool May(string permission) => access?.Can(permission, terminal.SiteId) == true;

        // Stops waiting for a reason on the line (#149), open first, and the reasons to code them with for someone who may.
        var now = timeProvider.GetUtcNow();
        var toCode = line is null
            ? []
            : (await stops.HandleAsync(
                new ListStopsQuery(new StopFilter(terminal.SiteId, line.LineId, now - StationStops.Window, now.AddMinutes(1), UncodedOnly: true, IncludeMicroStops: false)),
                cancellationToken)).Stops.OrderBy(StationStops.Order).ToList();
        var reasonsByMachine = new Dictionary<Guid, IReadOnlyList<ReasonCodeDto>>();
        if (May(Permissions.DowntimeCode))
        {
            foreach (var equipmentId in toCode.Select(s => s.EquipmentId).Distinct())
            {
                reasonsByMachine[equipmentId] = await codingReasons.HandleAsync(new ListCodingReasonsQuery(equipmentId), cancellationToken);
            }
        }

        // What may be called for (#157), by someone who may call; the line's calls come with it from the display (#158).
        var types = line is not null && May(Permissions.AndonCall)
            ? (await andonTypes.HandleAsync(new ListAndonTypesQuery(terminal.SiteId), cancellationToken)).Where(t => t.IsActive).ToList()
            : [];

        return new StationDto
        {
            TerminalName = terminal.Name,
            TerminalCode = terminal.Code,
            AuthMode = terminal.AuthMode,
            Language = terminal.Language,
            Region = terminal.Region,
            Lines = [.. lines.Select(l => new StationLineDto(l.LineId, l.Code, l.Name))],
            Line = line,
            RunningJob = row?.Running,
            Next = row?.Next ?? [],
            StopsToCode = toCode,
            CodingReasons = reasonsByMachine,
            AndonCalls = line?.Calls ?? [],
            AndonTypes = types,
            MaintenanceRequests = line?.Maintenance ?? [],
            RunEndReasons = query.PersonId is null ? [] : await reasons.HandleAsync(new ListReasonCodesQuery("run_end", terminal.SiteId), cancellationToken),
            Report = row?.Running is { JobType: "production" } running && May(Permissions.ProductionReport)
                ? await ReportAsync(running.JobId, terminal.SiteId, cancellationToken)
                : null,
            PersonId = person?.PersonId,
            PersonName = person?.PersonName,
            PersonIsUser = access is not null,
            CanRunJobs = May(Permissions.JobExecute),
            CanReport = May(Permissions.ProductionReport),
            CanCodeStops = May(Permissions.DowntimeCode),
            CanCallForHelp = May(Permissions.AndonCall),
            CanAnswerCalls = May(Permissions.AndonRespond),
            CanRequestMaintenance = line is not null && May(Permissions.MaintenanceRequest),
            IdleMinutes = (int)Domain.Labor.TerminalRules.StationIdle.TotalMinutes,
        };
    }

    // Reporting at the station (#148): the job as the Floor sees it, the machine's count since the last report, locations and scrap reasons.
    private async Task<StationReportDto> ReportAsync(Guid jobId, Guid siteId, CancellationToken cancellationToken)
    {
        var job = await execution.HandleAsync(new GetJobExecutionQuery(jobId), cancellationToken);
        var places = (await locations.HandleAsync(new ListInventoryLocationsQuery(siteId), cancellationToken)).Where(l => l.IsActive).ToList();
        var filling = job.FillingLots.Count > 0 ? job.FillingLots[0].LocationCode : null;
        return new StationReportDto
        {
            Job = job,
            Machine = job.BatchSize is null ? await machine.HandleAsync(new GetMachineCountsQuery(jobId, null), cancellationToken) : null,
            Locations = places,
            ScrapReasons = await reasons.HandleAsync(new ListReasonCodesQuery("scrap", siteId), cancellationToken),
            // The lot being filled's location, else the line's default output location (#122).
            DefaultLocationId = (places.FirstOrDefault(l => l.Code == filling) ?? places.FirstOrDefault(l => l.Id == job.DefaultLocationId))?.Id,
        };
    }
}

/// <summary>Which line a station serves (#146). Pure.</summary>
public static class StationLine
{
    public static LineDisplayLineDto? Choose(IReadOnlyList<LineDisplayLineDto> lines, string? code, Guid? defaultLineId)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            return lines.FirstOrDefault(l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return lines.FirstOrDefault(l => l.LineId == defaultLineId) ?? (lines.Count == 1 ? lines[0] : null);
    }
}

/// <summary>Which jobs a station may act on (#147). Pure.</summary>
public static class StationJobs
{
    /// <summary>The job running on the station's line, or one of its next jobs: nothing else can be started or stopped from there.</summary>
    public static bool IsOnLine(StationDto station, Guid jobId) =>
        station.RunningJob?.JobId == jobId || station.Next.Any(j => j.JobId == jobId);
}

/// <summary>What a report at a station did, in words (#148). Pure.</summary>
public static class StationReporting
{
    /// <summary>"Reported 1,200 m good and 12 m scrap; good output in lot WO-1-10-03."</summary>
    public static MessageDto Done(decimal good, decimal scrap, string? unit, ReportProductionResponse result)
    {
        string Qty(decimal value) => value.ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture) + (unit is null ? "" : " " + unit);
        var (g, s) = (Qty(good), Qty(scrap));
        var lots = string.Join(", ", result.Lots.Select(l => l.LotNumber ?? "the balance"));

        // Whole sentences, one per case, so each translates as a sentence (#189).
        return (good > 0 && scrap > 0, good > 0, result.Lots.Count) switch
        {
            (true, _, 0) => Notes.Of($"Reported {g} good and {s} scrap."),
            (true, _, 1) => Notes.Of($"Reported {g} good and {s} scrap; good output in lot {lots}."),
            (true, _, _) => Notes.Of($"Reported {g} good and {s} scrap; good output in lots {lots}."),
            (false, true, 0) => Notes.Of($"Reported {g} good."),
            (false, true, 1) => Notes.Of($"Reported {g} good; good output in lot {lots}."),
            (false, true, _) => Notes.Of($"Reported {g} good; good output in lots {lots}."),
            _ => Notes.Of($"Reported {s} scrap."),
        };
    }
}

/// <summary>The stops a station asks to code (#149). Pure.</summary>
public static class StationStops
{
    /// <summary>How far back a station looks for stops without a reason: a long shift.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(12);

    /// <summary>The open stop first, then the most recent.</summary>
    public static long Order(DowntimeDto stop) => stop.EndedAt is null ? long.MinValue : -stop.StartedAt.UtcTicks;
}

/// <summary>What andon at a station did, in words (#157). Pure.</summary>
public static class StationAndon
{
    /// <summary>"Maintenance is called." or, when one was waiting already, "Maintenance was called already; that call is still waiting."</summary>
    public static MessageDto Called(string typeName, AndonCallResponse result) => (result.AlreadyOpen, result.Status == Domain.Andon.AndonStatuses.Open) switch
    {
        (true, true) => Notes.Of($"{typeName} was called already; that call is still waiting."),
        (true, false) => Notes.Of($"{typeName} was called already; that call is still being answered."),
        _ => Notes.Of($"{typeName} is called."),
    };
}
