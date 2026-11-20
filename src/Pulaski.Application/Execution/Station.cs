using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Application.Security;
using Pulaski.Domain.Security;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Labor;

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
    ISecurityReadStore security,
    TimeProvider timeProvider) : IQueryHandler<GetStationQuery, StationDto>
{
    public async Task<StationDto> HandleAsync(GetStationQuery query, CancellationToken cancellationToken)
    {
        var terminal = query.Terminal;
        var lines = (await display.HandleAsync(new GetLineDisplayQuery(terminal.SiteId, terminal.ScopeEquipmentId, terminal.Name), cancellationToken)).Lines;
        var line = StationLine.Choose(lines, query.LineCode, terminal.DefaultEquipmentId);
        var next = line is null
            ? []
            : (await board.HandleAsync(new GetLineBoardQuery(terminal.SiteId), cancellationToken)).FirstOrDefault(r => r.LineId == line.LineId)?.Next ?? [];
        var person = query.PersonId is { } personId ? await status.HandleAsync(new GetTerminalStatusQuery(terminal, personId), cancellationToken) : null;

        // What they may do here is their user's role at the terminal's site (#150); without a user, only labor.
        var access = query.PersonId is { } id ? await security.GetPersonAccessAsync(id, timeProvider.GetUtcNow(), cancellationToken) : null;
        bool May(string permission) => access?.Can(permission, terminal.SiteId) == true;

        return new StationDto
        {
            TerminalName = terminal.Name,
            TerminalCode = terminal.Code,
            AuthMode = terminal.AuthMode,
            Lines = [.. lines.Select(l => new StationLineDto(l.LineId, l.Code, l.Name))],
            Line = line,
            Next = next,
            PersonId = person?.PersonId,
            PersonName = person?.PersonName,
            PersonIsUser = access is not null,
            CanRunJobs = May(Permissions.JobExecute),
            CanReport = May(Permissions.ProductionReport),
            CanCodeStops = May(Permissions.DowntimeCode),
            IdleMinutes = (int)Domain.Labor.TerminalRules.StationIdle.TotalMinutes,
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
