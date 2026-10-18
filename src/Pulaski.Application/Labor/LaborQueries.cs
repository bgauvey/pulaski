using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Labor;

public sealed record ListCrewsQuery(Guid SiteId) : IQuery<IReadOnlyList<CrewDto>>;

public sealed record ListPeopleQuery(Guid SiteId) : IQuery<IReadOnlyList<PersonDto>>;

/// <summary>The signed-in user as a person, and their open sessions; null person when they aren't set up as one.</summary>
public sealed record GetMyLaborQuery(Guid? UserId) : IQuery<MyLaborDto>;

/// <summary>Each line of a site with its shift, crew and who is on it now (D-040).</summary>
public sealed record ListLineLaborQuery(Guid SiteId) : IQuery<IReadOnlyList<LineLaborDto>>;

/// <summary>The shifts worked at a site on a day, for review and approval.</summary>
public sealed record ListLaborShiftsQuery(Guid SiteId, DateOnly Date) : IQuery<IReadOnlyList<LaborShiftDto>>;

public sealed record GetShiftLaborQuery(ShiftKey Shift) : IQuery<ShiftLaborDto>;

/// <summary>Labor between two production days (D-040): by job against its standard, by person and by shift.</summary>
public sealed record GetLaborReportQuery(Guid SiteId, DateOnly From, DateOnly To) : IQuery<LaborReportDto>;

internal static class LaborView
{
    public static LaborSessionDto ToDto(SessionRow row, DateTimeOffset now) => new()
    {
        Id = row.Session.Id,
        PersonId = row.Session.PersonId,
        PersonName = row.PersonName,
        EmployeeNumber = row.EmployeeNumber,
        EquipmentId = row.Session.EquipmentId,
        EquipmentCode = row.EquipmentCode,
        JobId = row.Session.JobId,
        JobNumber = row.JobNumber,
        LaborType = row.Session.LaborType,
        ReasonCodeId = row.Session.ReasonCodeId,
        ReasonName = row.ReasonName,
        StartedAt = row.Session.StartedAt,
        EndedAt = row.Session.EndedAt,
        AllocationPct = row.Session.AllocationPct,
        ProductionDate = row.Session.ProductionDate,
        ShiftScheduleId = row.Session.ShiftScheduleId,
        ShiftCode = row.Session.ShiftCode,
        Source = row.Session.Source,
        CorrectsSessionId = row.Session.CorrectsSessionId,
        IsVoid = row.Session.IsVoid,
        IsSuperseded = row.IsSuperseded,
        IsApproved = row.IsApproved,
        Note = row.Session.Note,
        RecordedBy = row.Session.RecordedBy,
        Hours = row.Session.IsVoid ? 0 : Math.Round(LaborRules.Hours(row.Session.StartedAt, row.Session.EndedAt ?? now) * row.Session.AllocationPct / 100m, 2),
        Version = row.Version,
    };

    /// <summary>The sessions that count: neither replaced by a correction nor a voiding correction.</summary>
    public static IEnumerable<SessionRow> Counting(IEnumerable<SessionRow> rows) => rows.Where(r => !r.IsSuperseded && !r.Session.IsVoid);

    public static TimeZoneInfo Zone(string? timeZone) => TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "UTC");

    public static DateTimeOffset DayStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The shifts in the sessions' keys, named from the site's schedules, with their approval.</summary>
    public static async Task<IReadOnlyList<LaborShiftDto>> ShiftsAsync(
        ILaborReadStore store, Guid siteId, IReadOnlyList<SessionRow> rows, DateOnly first, DateOnly last, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var zone = Zone(await store.GetSiteTimeZoneAsync(siteId, cancellationToken));
        var schedules = await store.ListSchedulesAsync(siteId, cancellationToken);
        var approvals = (await store.ListApprovalsAsync(siteId, first, last, cancellationToken)).ToDictionary(a => a.Shift);

        var result = new List<LaborShiftDto>();
        foreach (var group in rows.GroupBy(r => r.Session.Shift))
        {
            var key = group.Key;
            ShiftOccurrence? occurrence = null;
            string? scheduleCode = null;
            if (key.ShiftScheduleId is { } scheduleId && schedules.FirstOrDefault(s => s.Id == scheduleId) is { Schedule: { } schedule } found)
            {
                scheduleCode = found.Code;
                var dayStart = DayStart(key.ProductionDate, zone);
                occurrence = schedule.Expand(zone, dayStart, dayStart.AddDays(1)).FirstOrDefault(o => o.Code == key.ShiftCode && o.ProductionDate == key.ProductionDate);
            }

            var counting = Counting(group).ToList();
            approvals.TryGetValue(key, out var approval);
            result.Add(new LaborShiftDto
            {
                ProductionDate = key.ProductionDate,
                ShiftScheduleId = key.ShiftScheduleId,
                ScheduleCode = scheduleCode,
                ShiftCode = key.ShiftCode,
                ShiftName = occurrence?.Name,
                Start = occurrence is null ? null : TimeZoneInfo.ConvertTime(occurrence.Start, zone),
                End = occurrence is null ? null : TimeZoneInfo.ConvertTime(occurrence.End, zone),
                CrewCode = occurrence?.Crew,
                Sessions = counting.Count,
                OpenSessions = counting.Count(r => r.Session.EndedAt is null),
                Hours = Math.Round(counting.Sum(r => LaborRules.Hours(r.Session.StartedAt, r.Session.EndedAt ?? now) * r.Session.AllocationPct / 100m), 2),
                IsApproved = approval is not null,
                ApprovedBy = approval?.ApprovedBy,
                ApprovedAt = approval?.ApprovedAt,
            });
        }

        return [.. result.OrderBy(s => s.ProductionDate).ThenBy(s => s.Start ?? DateTimeOffset.MaxValue).ThenBy(s => s.ShiftCode, StringComparer.Ordinal)];
    }
}

internal sealed class ListCrewsHandler(ILaborReadStore store) : IQueryHandler<ListCrewsQuery, IReadOnlyList<CrewDto>>
{
    public Task<IReadOnlyList<CrewDto>> HandleAsync(ListCrewsQuery query, CancellationToken cancellationToken) => store.ListCrewsAsync(query.SiteId, cancellationToken);
}

internal sealed class ListPeopleHandler(ILaborReadStore store) : IQueryHandler<ListPeopleQuery, IReadOnlyList<PersonDto>>
{
    public Task<IReadOnlyList<PersonDto>> HandleAsync(ListPeopleQuery query, CancellationToken cancellationToken) => store.ListPeopleAsync(query.SiteId, cancellationToken);
}

internal sealed class GetMyLaborHandler(ILaborReadStore store, TimeProvider timeProvider) : IQueryHandler<GetMyLaborQuery, MyLaborDto>
{
    public async Task<MyLaborDto> HandleAsync(GetMyLaborQuery query, CancellationToken cancellationToken)
    {
        var person = query.UserId is { } userId ? await store.GetPersonOfUserAsync(userId, cancellationToken) : null;
        if (person is null)
        {
            return new MyLaborDto(null, []);
        }

        var now = timeProvider.GetUtcNow();
        var open = await store.ListOpenSessionsAsync(person.SiteId, person.Id, cancellationToken);
        return new MyLaborDto(person, [.. open.Select(r => LaborView.ToDto(r, now))]);
    }
}

internal sealed class ListLineLaborHandler(ILaborReadStore store, TimeProvider timeProvider) : IQueryHandler<ListLineLaborQuery, IReadOnlyList<LineLaborDto>>
{
    public async Task<IReadOnlyList<LineLaborDto>> HandleAsync(ListLineLaborQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lines = await store.ListLinesAsync(query.SiteId, cancellationToken);
        var open = await store.ListOpenSessionsAsync(query.SiteId, null, cancellationToken);
        var shifts = (await store.ListEquipmentShiftsAsync([.. lines.Select(l => l.Id)], cancellationToken)).ToDictionary(e => e.EquipmentId);
        var crews = await store.CrewNamesAsync(query.SiteId, cancellationToken);

        return [.. lines.Select(line =>
        {
            CurrentShiftDto? current = null;
            if (shifts.TryGetValue(line.Id, out var calendar) && calendar is { Schedule: { } schedule, ScheduleId: { } scheduleId }
                && schedule.ShiftAt(calendar.Zone, now, TimeSpan.Zero) is { } shift)
            {
                current = new CurrentShiftDto
                {
                    ShiftScheduleId = scheduleId,
                    ScheduleCode = calendar.ScheduleCode ?? schedule.Code,
                    ShiftCode = shift.Code,
                    ShiftName = shift.Name,
                    ProductionDate = shift.ProductionDate,
                    Start = TimeZoneInfo.ConvertTime(shift.Start, calendar.Zone),
                    End = TimeZoneInfo.ConvertTime(shift.End, calendar.Zone),
                    CrewCode = shift.Crew,
                    CrewName = shift.Crew is { } crew ? crews.GetValueOrDefault(crew) : null,
                };
            }

            return new LineLaborDto
            {
                LineId = line.Id,
                LineCode = line.Code,
                LineName = line.Name,
                Shift = current,
                RunningJobId = line.RunningJobId,
                RunningJobNumber = line.RunningJobNumber,
                StandardCrewSize = line.StandardCrewSize,
                Sessions = [.. open.Where(r => r.Session.EquipmentId == line.Id).Select(r => LaborView.ToDto(r, now))],
            };
        })];
    }
}

internal sealed class ListLaborShiftsHandler(ILaborReadStore store, TimeProvider timeProvider) : IQueryHandler<ListLaborShiftsQuery, IReadOnlyList<LaborShiftDto>>
{
    public async Task<IReadOnlyList<LaborShiftDto>> HandleAsync(ListLaborShiftsQuery query, CancellationToken cancellationToken)
    {
        var rows = await store.ListSessionsAsync(query.SiteId, query.Date, query.Date, cancellationToken);
        return await LaborView.ShiftsAsync(store, query.SiteId, rows, query.Date, query.Date, timeProvider.GetUtcNow(), cancellationToken);
    }
}

internal sealed class GetShiftLaborHandler(ILaborReadStore store, TimeProvider timeProvider) : IQueryHandler<GetShiftLaborQuery, ShiftLaborDto>
{
    public async Task<ShiftLaborDto> HandleAsync(GetShiftLaborQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var key = query.Shift;
        var rows = (await store.ListSessionsAsync(key.SiteId, key.ProductionDate, key.ProductionDate, cancellationToken))
            .Where(r => r.Session.Shift == key)
            .ToList();
        var shifts = await LaborView.ShiftsAsync(store, key.SiteId, rows, key.ProductionDate, key.ProductionDate, now, cancellationToken);
        var shift = shifts.Count > 0 ? shifts[0] : new LaborShiftDto { ProductionDate = key.ProductionDate, ShiftScheduleId = key.ShiftScheduleId, ShiftCode = key.ShiftCode };
        return new ShiftLaborDto
        {
            Shift = shift,
            Sessions = [.. rows.OrderBy(r => r.EquipmentCode, StringComparer.Ordinal).ThenBy(r => r.PersonName, StringComparer.Ordinal).ThenBy(r => r.Session.StartedAt)
                .Select(r => LaborView.ToDto(r, now))],
        };
    }
}

internal sealed class GetLaborReportHandler(ILaborReadStore store, TimeProvider timeProvider) : IQueryHandler<GetLaborReportQuery, LaborReportDto>
{
    public const int MaxDays = 93;

    public async Task<LaborReportDto> HandleAsync(GetLaborReportQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (from, to) = query.From <= query.To ? (query.From, query.To) : (query.To, query.From);
        if (to.DayNumber - from.DayNumber > MaxDays)
        {
            from = to.AddDays(-MaxDays);
        }

        var rows = await store.ListSessionsAsync(query.SiteId, from, to, cancellationToken);
        var counting = LaborView.Counting(rows).ToList();
        var spans = counting.Select(r => new LaborSpan(r.Session.Id, r.Session.PersonId, r.Session.EquipmentId, r.Session.JobId, r.Session.LaborType,
            r.Session.StartedAt, r.Session.EndedAt, r.Session.AllocationPct)).ToList();

        var runs = spans.Count == 0
            ? []
            : await store.ListRunsAsync(query.SiteId, spans.Min(s => s.Start), spans.Max(s => s.End ?? now), cancellationToken);
        var shares = LaborRules.Attribute(spans, [.. runs.Select(r => new RunSpan(r.RunId, r.JobId, r.EquipmentId, r.StartedAt, r.EndedAt))], now);

        // By job: the runs that had labor or ran in the window, against the standard crew.
        var jobs = runs.GroupBy(r => r.JobId).Select(g =>
        {
            var first = g.First();
            var runHours = g.Sum(r => LaborRules.Hours(r.StartedAt, r.EndedAt ?? now));
            var standard = g.Where(r => r.StandardCrewSize is not null).Sum(r => LaborRules.Hours(r.StartedAt, r.EndedAt ?? now) * r.StandardCrewSize!.Value);
            var direct = shares.Where(s => s.JobId == g.Key && s.LaborType == LaborTypes.Direct).Sum(s => s.Hours);
            var setup = shares.Where(s => s.JobId == g.Key && s.LaborType == LaborTypes.Setup).Sum(s => s.Hours);
            var hasStandard = g.Any(r => r.StandardCrewSize is not null);
            return new JobLaborRowDto
            {
                JobId = g.Key,
                JobNumber = first.JobNumber,
                MaterialCode = first.MaterialCode,
                LineCode = first.LineCode,
                RunHours = Math.Round(runHours, 2),
                DirectHours = Math.Round(direct, 2),
                SetupHours = Math.Round(setup, 2),
                StandardHours = hasStandard ? Math.Round(standard, 2) : null,
                EfficiencyPct = hasStandard && direct > 0 ? Math.Round(standard / direct * 100m, 1) : null,
            };
        }).Where(j => j.DirectHours > 0 || j.SetupHours > 0)
          .OrderBy(j => j.LineCode, StringComparer.Ordinal).ThenBy(j => j.JobNumber, StringComparer.Ordinal).ToList();

        // Setup on a job that didn't run in the window still shows.
        foreach (var setupOnly in shares.Where(s => s.JobId is { } id && s.RunId is null && s.LaborType == LaborTypes.Setup && jobs.All(j => j.JobId != id)).GroupBy(s => s.JobId!.Value))
        {
            var session = counting.First(r => r.Session.JobId == setupOnly.Key);
            jobs.Add(new JobLaborRowDto
            {
                JobId = setupOnly.Key,
                JobNumber = session.JobNumber ?? "",
                LineCode = session.EquipmentCode,
                SetupHours = Math.Round(setupOnly.Sum(s => s.Hours), 2),
            });
        }

        var bySession = counting.ToDictionary(r => r.Session.Id);
        var people = shares.GroupBy(s => bySession[s.SessionId].Session.PersonId).Select(g =>
        {
            var row = bySession[g.First().SessionId];
            decimal Of(string type) => Math.Round(g.Where(s => s.LaborType == type).Sum(s => s.Hours), 2);
            return new PersonLaborRowDto
            {
                PersonId = g.Key,
                PersonName = row.PersonName,
                EmployeeNumber = row.EmployeeNumber,
                DirectHours = Of(LaborTypes.Direct),
                SetupHours = Of(LaborTypes.Setup),
                IndirectHours = Of(LaborTypes.Indirect),
                TrainingHours = Of(LaborTypes.Training),
                IdleHours = Math.Round(g.Where(s => s.LaborType == LaborTypes.Direct && s.RunId is null).Sum(s => s.Hours), 2),
                TotalHours = Math.Round(g.Sum(s => s.Hours), 2),
            };
        }).OrderBy(p => p.PersonName, StringComparer.Ordinal).ToList();

        return new LaborReportDto
        {
            From = from,
            To = to,
            Jobs = jobs,
            People = people,
            Shifts = await LaborView.ShiftsAsync(store, query.SiteId, rows, from, to, now, cancellationToken),
        };
    }
}
