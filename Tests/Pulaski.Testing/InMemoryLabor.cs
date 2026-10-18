using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Shifts;

namespace Pulaski.Testing;

/// <summary>In-memory crews, people and labor (D-040). Lines are added with the schedule they follow, if any.</summary>
public sealed class InMemoryLabor : ILaborRepository, ILaborReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, string> _sites = [];
    private readonly Dictionary<Guid, Versioned<CrewRecord>> _crews = [];
    private readonly Dictionary<Guid, Versioned<PersonRecord>> _people = [];
    private readonly Dictionary<Guid, Versioned<SessionRecord>> _sessions = [];
    private readonly Dictionary<Guid, (Guid SiteId, string Code, Guid? ScheduleId)> _equipment = [];
    private readonly Dictionary<Guid, (string Code, ShiftSchedule Schedule)> _schedules = [];
    private readonly Dictionary<Guid, LaborJob> _jobs = [];
    private readonly Dictionary<Guid, Guid?> _reasons = [];
    private readonly Dictionary<string, Guid> _users = [];
    private readonly List<ApprovalRow> _approvals = [];
    private readonly List<RunRow> _runs = [];
    private long _next = 1;

    public void AddSite(Guid siteId, string timeZone = "UTC")
    {
        lock (_lock)
        {
            _sites[siteId] = timeZone;
        }
    }

    public Guid AddSchedule(ShiftSchedule schedule)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _schedules[id] = (schedule.Code, schedule);
        }

        return id;
    }

    public Guid AddLine(Guid siteId, string code, Guid? scheduleId = null)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _equipment[id] = (siteId, code, scheduleId);
        }

        return id;
    }

    public Guid AddJob(Guid siteId, string jobNumber, Guid? lineId = null)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _jobs[id] = new LaborJob(id, siteId, jobNumber, lineId);
        }

        return id;
    }

    public Guid AddReason(Guid? siteId = null)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _reasons[id] = siteId;
        }

        return id;
    }

    public void AddUser(string username, Guid userId)
    {
        lock (_lock)
        {
            _users[username] = userId;
        }
    }

    public void AddRun(RunRow run)
    {
        lock (_lock)
        {
            _runs.Add(run);
        }
    }

    public Guid AddPerson(Guid siteId, string name, Guid? crewId = null, Guid? userId = null)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _people[id] = new(new PersonRecord(id, siteId, "E" + _next, name, crewId, userId, true), ++_next);
        }

        return id;
    }

    public IReadOnlyList<SessionRecord> Sessions()
    {
        lock (_lock)
        {
            return [.. _sessions.Values.Select(s => s.Value).OrderBy(s => s.StartedAt)];
        }
    }

    public Versioned<SessionRecord> Session(Guid id)
    {
        lock (_lock)
        {
            return _sessions[id];
        }
    }

    // ---- ILaborRepository ----

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => Locked(() => _sites.ContainsKey(siteId));

    public Task<Versioned<CrewRecord>?> GetCrewForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _crews.GetValueOrDefault(id));

    public Task<bool> CrewCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        Locked(() => _crews.Values.Any(c => c.Value.SiteId == siteId && c.Value.Code == code));

    public Task<long> InsertCrewAsync(CrewRecord crew, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_crews, crew.Id, crew));

    public Task<long> UpdateCrewAsync(CrewRecord crew, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_crews, crew.Id, crew));

    public Task<Versioned<PersonRecord>?> GetPersonForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _people.GetValueOrDefault(id));

    public Task<IReadOnlyList<PersonRecord>> GetPeopleForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<PersonRecord>>(() => [.. ids.Where(_people.ContainsKey).Select(id => _people[id].Value)]);

    public Task<Versioned<PersonRecord>?> GetPersonOfUserForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        Locked(() => _people.Values.FirstOrDefault(p => p.Value.AppUserId == userId));

    public Task<bool> EmployeeNumberExistsAsync(Guid siteId, string employeeNumber, CancellationToken cancellationToken) =>
        Locked(() => _people.Values.Any(p => p.Value.SiteId == siteId && p.Value.EmployeeNumber == employeeNumber));

    public Task<Guid?> FindLocalUserIdAsync(string username, CancellationToken cancellationToken) =>
        Locked(() => _users.TryGetValue(username, out var id) ? id : (Guid?)null);

    public Task<bool> UserIsAnotherPersonAsync(Guid userId, Guid exceptPersonId, CancellationToken cancellationToken) =>
        Locked(() => _people.Values.Any(p => p.Value.AppUserId == userId && p.Value.Id != exceptPersonId));

    public Task<long> InsertPersonAsync(PersonRecord person, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_people, person.Id, person));

    public Task<long> UpdatePersonAsync(PersonRecord person, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_people, person.Id, person));

    public Task<EquipmentShifts?> GetEquipmentShiftsAsync(Guid equipmentId, CancellationToken cancellationToken) => Locked(() => Shifts(equipmentId));

    public Task<ShiftSchedule?> GetScheduleAsync(Guid scheduleId, CancellationToken cancellationToken) =>
        Locked(() => _schedules.TryGetValue(scheduleId, out var s) ? s.Schedule : null);

    public Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken) => Locked(() => _sites.GetValueOrDefault(siteId));

    public Task<LaborJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken) => Locked(() => _jobs.GetValueOrDefault(jobId));

    public Task<bool> IsIndirectReasonAsync(Guid reasonCodeId, Guid siteId, CancellationToken cancellationToken) =>
        Locked(() => _reasons.TryGetValue(reasonCodeId, out var site) && (site is null || site == siteId));

    public Task<Versioned<SessionRecord>?> GetSessionForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _sessions.GetValueOrDefault(id));

    public Task<IReadOnlyList<SessionRecord>> ListOpenSessionsOfPersonForUpdateAsync(Guid personId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<SessionRecord>>(() => [.. _sessions.Values.Select(s => s.Value).Where(s => s.PersonId == personId && s.EndedAt is null)]);

    public Task<IReadOnlyList<Versioned<SessionRecord>>> ListOpenSessionsOnEquipmentForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<Versioned<SessionRecord>>>(() => [.. _sessions.Values.Where(s => s.Value.EquipmentId == equipmentId && s.Value.EndedAt is null)]);

    public Task<IReadOnlyList<SessionRecord>> ListShiftSessionsForUpdateAsync(ShiftKey shift, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<SessionRecord>>(() => [.. _sessions.Values.Select(s => s.Value).Where(s => s.Shift == shift)]);

    public Task<bool> IsCorrectedAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Locked(() => _sessions.Values.Any(s => s.Value.CorrectsSessionId == sessionId));

    public Task<long> InsertSessionAsync(SessionRecord session, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_sessions, session.Id, session));

    public Task<long> UpdateSessionAsync(SessionRecord session, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_sessions, session.Id, session));

    public Task<bool> IsApprovedAsync(ShiftKey shift, CancellationToken cancellationToken) => Locked(() => _approvals.Any(a => a.Shift == shift));

    public Task InsertApprovalAsync(Guid id, ShiftKey shift, string approvedBy, Guid? approvedByUserId, DateTimeOffset at, string? note, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _approvals.Add(new ApprovalRow(shift, approvedBy, at));
        }

        return Task.CompletedTask;
    }

    // ---- ILaborReadStore ----

    public Task<IReadOnlyList<CrewDto>> ListCrewsAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<CrewDto>>(() => [.. _crews.Values.Where(c => c.Value.SiteId == siteId).Select(c => new CrewDto
        {
            Id = c.Value.Id, SiteId = c.Value.SiteId, Code = c.Value.Code, Name = c.Value.Name, IsActive = c.Value.IsActive,
            Members = _people.Values.Count(p => p.Value.CrewId == c.Value.Id && p.Value.IsActive), Version = c.Version,
        })]);

    public Task<IReadOnlyList<PersonDto>> ListPeopleAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<PersonDto>>(() => [.. _people.Values.Where(p => p.Value.SiteId == siteId).Select(ToDto)]);

    public Task<PersonDto?> GetPersonOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Locked(() => _people.Values.FirstOrDefault(p => p.Value.AppUserId == userId) is { } p ? ToDto(p) : null);

    public Task<IReadOnlyList<LaborLineRow>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<LaborLineRow>>(() => [.. _equipment.Where(e => e.Value.SiteId == siteId).Select(e => new LaborLineRow(e.Key, e.Value.Code, e.Value.Code, null, null, null))]);

    public Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<EquipmentShifts>>(() => [.. equipmentIds.Select(Shifts).OfType<EquipmentShifts>()]);

    public Task<IReadOnlyList<SessionRow>> ListOpenSessionsAsync(Guid siteId, Guid? personId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<SessionRow>>(() => [.. Rows(s => s.SiteId == siteId && s.EndedAt is null && !s.IsVoid && (personId is null || s.PersonId == personId)).Where(r => !r.IsSuperseded)]);

    public Task<IReadOnlyList<SessionRow>> ListSessionsAsync(Guid siteId, DateOnly first, DateOnly last, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<SessionRow>>(() => [.. Rows(s => s.SiteId == siteId && s.ProductionDate >= first && s.ProductionDate <= last)]);

    public Task<IReadOnlyList<RunRow>> ListRunsAsync(Guid siteId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<RunRow>>(() => [.. _runs.Where(r => r.StartedAt < windowEnd && (r.EndedAt is null || r.EndedAt > windowStart))]);

    public Task<IReadOnlyList<ApprovalRow>> ListApprovalsAsync(Guid siteId, DateOnly first, DateOnly last, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<ApprovalRow>>(() => [.. _approvals.Where(a => a.Shift.SiteId == siteId && a.Shift.ProductionDate >= first && a.Shift.ProductionDate <= last)]);

    public Task<IReadOnlyList<(Guid Id, string Code, ShiftSchedule Schedule)>> ListSchedulesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<(Guid Id, string Code, ShiftSchedule Schedule)>>(() => [.. _schedules.Select(s => (s.Key, s.Value.Code, s.Value.Schedule))]);

    public Task<IReadOnlyDictionary<string, string>> CrewNamesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyDictionary<string, string>>(() => _crews.Values.Where(c => c.Value.SiteId == siteId).ToDictionary(c => c.Value.Code, c => c.Value.Name));

    // ---- helpers ----

    private EquipmentShifts? Shifts(Guid equipmentId)
    {
        if (!_equipment.TryGetValue(equipmentId, out var e))
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(_sites.GetValueOrDefault(e.SiteId, "UTC"));
        var schedule = e.ScheduleId is { } id ? _schedules[id] : ((string Code, ShiftSchedule Schedule)?)null;
        return new EquipmentShifts(equipmentId, e.SiteId, e.Code, zone, e.ScheduleId, schedule?.Code, schedule?.Schedule);
    }

    private IEnumerable<SessionRow> Rows(Func<SessionRecord, bool> filter) =>
        _sessions.Values.Where(s => filter(s.Value)).OrderBy(s => s.Value.StartedAt).Select(s => new SessionRow
        {
            Session = s.Value,
            PersonName = _people[s.Value.PersonId].Value.DisplayName,
            EmployeeNumber = _people[s.Value.PersonId].Value.EmployeeNumber,
            EquipmentCode = _equipment.TryGetValue(s.Value.EquipmentId, out var e) ? e.Code : "?",
            JobNumber = s.Value.JobId is { } j && _jobs.TryGetValue(j, out var job) ? job.JobNumber : null,
            IsSuperseded = _sessions.Values.Any(c => c.Value.CorrectsSessionId == s.Value.Id),
            IsApproved = _approvals.Any(a => a.Shift == s.Value.Shift),
            Version = s.Version,
        });

    private PersonDto ToDto(Versioned<PersonRecord> p) => new()
    {
        Id = p.Value.Id,
        SiteId = p.Value.SiteId,
        EmployeeNumber = p.Value.EmployeeNumber,
        DisplayName = p.Value.DisplayName,
        CrewId = p.Value.CrewId,
        CrewCode = p.Value.CrewId is { } c && _crews.TryGetValue(c, out var crew) ? crew.Value.Code : null,
        Username = p.Value.AppUserId is { } u ? _users.FirstOrDefault(x => x.Value == u).Key : null,
        IsActive = p.Value.IsActive,
        Version = p.Version,
    };

    private long Save<T>(Dictionary<Guid, Versioned<T>> table, Guid id, T value)
    {
        var version = ++_next;
        table[id] = new Versioned<T>(value, version);
        return version;
    }

    private Task<T> Locked<T>(Func<T> read)
    {
        lock (_lock)
        {
            return Task.FromResult(read());
        }
    }
}
