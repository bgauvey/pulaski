using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;

namespace Pulaski.Testing;

/// <summary>
/// In-memory crews, people, labor, breaks and qualifications (D-040, D-047, D-048). Lines are added with the schedule they
/// follow, if any, and optionally the equipment above them.
/// </summary>
public sealed class InMemoryLabor : ILaborRepository, ILaborReadStore, IQualificationRepository, IQualificationReadStore
{
    private readonly Dictionary<Guid, Versioned<BreakRecord>> _breaks = [];
    private readonly Dictionary<Guid, Versioned<QualificationRecord>> _qualifications = [];
    private readonly Dictionary<Guid, Versioned<GrantRecord>> _grants = [];
    private readonly Dictionary<(Guid Equipment, Guid Qualification), bool> _requirements = [];
    private readonly Dictionary<Guid, Guid> _parents = [];
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

    public Guid AddLine(Guid siteId, string code, Guid? scheduleId = null) => AddLineWithId(Guid.CreateVersion7(), siteId, code, scheduleId);

    /// <summary>A line with a given id, so the equipment store can know it too.</summary>
    public Guid AddLineWithId(Guid id, Guid siteId, string code, Guid? scheduleId = null)
    {
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

    public Guid AddCrew(Guid siteId, string code, bool isActive = true)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _crews[id] = new(new CrewRecord(id, siteId, code, code, isActive), ++_next);
        }

        return id;
    }

    public Versioned<PersonRecord> Person(Guid id)
    {
        lock (_lock)
        {
            return _people[id];
        }
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

    public Task<Versioned<PersonRecord>?> FindPersonByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken) =>
        Locked(() => _people.Values.FirstOrDefault(p => p.Value.ExternalSystem == externalSystem && p.Value.ExternalKey == externalKey));

    public Task<Versioned<PersonRecord>?> FindPersonByNumberForUpdateAsync(Guid siteId, string employeeNumber, CancellationToken cancellationToken) =>
        Locked(() => _people.Values.FirstOrDefault(p => p.Value.SiteId == siteId && p.Value.EmployeeNumber == employeeNumber));

    public Task<CrewRecord?> FindCrewByCodeAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        Locked(() => _crews.Values.Select(c => c.Value).FirstOrDefault(c => c.SiteId == siteId && c.Code == code));

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

    /// <summary>Puts <paramref name="childId"/> below <paramref name="parentId"/>, so it inherits its requirements.</summary>
    public void SetParent(Guid childId, Guid parentId)
    {
        lock (_lock)
        {
            _parents[childId] = parentId;
        }
    }

    public IReadOnlyList<BreakRecord> Breaks()
    {
        lock (_lock)
        {
            return [.. _breaks.Values.Select(b => b.Value).OrderBy(b => b.StartedAt)];
        }
    }

    public Versioned<BreakRecord> Break(Guid id)
    {
        lock (_lock)
        {
            return _breaks[id];
        }
    }

    public Versioned<GrantRecord> Grant(Guid id)
    {
        lock (_lock)
        {
            return _grants[id];
        }
    }

    // ---- Breaks (D-047) ----

    public Task<Versioned<BreakRecord>?> GetBreakForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _breaks.GetValueOrDefault(id));

    public Task<Versioned<BreakRecord>?> GetOpenBreakForUpdateAsync(Guid personId, CancellationToken cancellationToken) =>
        Locked(() => _breaks.Values.FirstOrDefault(b => b.Value.PersonId == personId && b.Value.EndedAt is null));

    public Task<IReadOnlyList<BreakRecord>> ListBreaksOfPersonAsync(Guid personId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<BreakRecord>>(() => [.. BreakRows(b => b.PersonId == personId && b.StartedAt < windowEnd && (b.EndedAt is null || b.EndedAt > windowStart))
            .Where(r => !r.IsSuperseded && !r.Break.IsVoid).Select(r => r.Break)]);

    public Task<IReadOnlyList<SessionRecord>> ListSessionsOfPersonAtAsync(Guid personId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<SessionRecord>>(() => [.. Rows(s => s.PersonId == personId && !s.IsVoid && s.StartedAt <= at && (s.EndedAt is null || s.EndedAt > at))
            .Where(r => !r.IsSuperseded).Select(r => r.Session)]);

    public Task<IReadOnlyList<BreakRecord>> ListShiftBreaksForUpdateAsync(ShiftKey shift, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<BreakRecord>>(() => [.. _breaks.Values.Where(b => b.Value.Shift == shift).Select(b => b.Value)]);

    public Task<bool> IsBreakCorrectedAsync(Guid breakId, CancellationToken cancellationToken) => Locked(() => _breaks.Values.Any(b => b.Value.CorrectsBreakId == breakId));

    public Task<long> InsertBreakAsync(BreakRecord record, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_breaks, record.Id, record));

    public Task<long> UpdateBreakAsync(BreakRecord record, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_breaks, record.Id, record));

    public Task<IReadOnlyList<BreakRow>> ListBreaksAsync(Guid siteId, Guid? personId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<BreakRow>>(() => [.. BreakRows(b => b.SiteId == siteId && (personId is null || b.PersonId == personId)
            && b.StartedAt < windowEnd && (b.EndedAt is null || b.EndedAt > windowStart))]);

    // ---- Qualifications (D-048) ----

    public Task<IReadOnlyList<RequiredQualification>> ListRequiredQualificationsAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<RequiredQualification>>(() => [.. Required(equipmentId).Select(r => new RequiredQualification(r.Q.Id, r.Q.Code, r.Q.Name))]);

    public Task<IReadOnlyList<QualificationGrant>> ListGrantsOfPersonAsync(Guid personId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<QualificationGrant>>(() => [.. _grants.Values.Where(g => g.Value.PersonId == personId).Select(g => DomainGrant(g.Value))]);

    public Task<IReadOnlyList<LineRequirement>> ListLineRequirementsAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<LineRequirement>>(() => [.. _equipment.Where(e => e.Value.SiteId == siteId)
            .SelectMany(e => Required(e.Key).Select(r => new LineRequirement(e.Key, new RequiredQualification(r.Q.Id, r.Q.Code, r.Q.Name))))]);

    public Task<IReadOnlyList<PersonGrant>> ListSiteGrantsAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<PersonGrant>>(() => [.. _grants.Values.Where(g => _people[g.Value.PersonId].Value.SiteId == siteId)
            .Select(g => new PersonGrant(g.Value.PersonId, DomainGrant(g.Value)))]);

    public Task<Versioned<QualificationRecord>?> GetQualificationForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _qualifications.GetValueOrDefault(id));

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        Locked(() => _qualifications.Values.Any(q => q.Value.SiteId == siteId && q.Value.Code == code));

    public Task<long> InsertQualificationAsync(QualificationRecord qualification, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() => Save(_qualifications, qualification.Id, qualification));

    public Task<long> UpdateQualificationAsync(QualificationRecord qualification, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() => Save(_qualifications, qualification.Id, qualification));

    public Task<Versioned<GrantRecord>?> GetGrantForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _grants.GetValueOrDefault(id));

    public Task<long> InsertGrantAsync(GrantRecord grant, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_grants, grant.Id, grant));

    public Task<long> UpdateGrantAsync(GrantRecord grant, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(_grants, grant.Id, grant));

    public Task<bool?> GetRequirementForUpdateAsync(Guid equipmentId, Guid qualificationId, CancellationToken cancellationToken) =>
        Locked(() => _requirements.TryGetValue((equipmentId, qualificationId), out var on) ? on : (bool?)null);

    public Task<long> SetRequirementAsync(Guid equipmentId, Guid qualificationId, bool isRequired, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            _requirements[(equipmentId, qualificationId)] = isRequired;
            return ++_next;
        });

    public Task<IReadOnlyList<QualificationDto>> ListQualificationsAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<QualificationDto>>(() => [.. _qualifications.Values.Where(q => q.Value.SiteId == siteId).OrderBy(q => q.Value.Code, StringComparer.Ordinal).Select(q => new QualificationDto
        {
            Id = q.Value.Id, SiteId = q.Value.SiteId, Code = q.Value.Code, Name = q.Value.Name, Description = q.Value.Description,
            ValidityMonths = q.Value.ValidityMonths, IsActive = q.Value.IsActive, Version = q.Version,
            RequiredOn = [.. _requirements.Where(r => r.Value && r.Key.Qualification == q.Value.Id).Select(r => _equipment.TryGetValue(r.Key.Equipment, out var e) ? e.Code : "?")],
        })]);

    public Task<IReadOnlyList<GrantRow>> ListGrantsAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<GrantRow>>(() => [.. _grants.Values.Where(g => _people[g.Value.PersonId].Value.SiteId == siteId).Select(g =>
        {
            var person = _people[g.Value.PersonId].Value;
            var q = _qualifications[g.Value.QualificationId].Value;
            return new GrantRow(g.Value, person.DisplayName, person.EmployeeNumber, person.SiteId, q.Code, q.Name, g.Version);
        })]);

    public Task<IReadOnlyList<RequirementRow>?> ListRequirementsAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<RequirementRow>?>(() => _equipment.ContainsKey(equipmentId)
            ? [.. Required(equipmentId).Select(r => new RequirementRow(r.Q.Id, r.Q.Code, r.Q.Name, r.From, _equipment[r.From].Code, r.From == equipmentId))]
            : null);

    private IEnumerable<(QualificationRecord Q, Guid From)> Required(Guid equipmentId)
    {
        for (Guid? at = equipmentId; at is { } id; at = _parents.TryGetValue(id, out var parent) ? parent : null)
        {
            foreach (var r in _requirements.Where(r => r.Value && r.Key.Equipment == id))
            {
                var q = _qualifications[r.Key.Qualification].Value;
                if (q.IsActive)
                {
                    yield return (q, id);
                }
            }
        }
    }

    private static QualificationGrant DomainGrant(GrantRecord g) => new(g.QualificationId, g.ValidFrom, g.ExpiresOn, g.RevokedAt);

    private IEnumerable<BreakRow> BreakRows(Func<BreakRecord, bool> filter) =>
        _breaks.Values.Where(b => filter(b.Value)).OrderBy(b => b.Value.StartedAt).Select(b => new BreakRow
        {
            Break = b.Value,
            PersonName = _people[b.Value.PersonId].Value.DisplayName,
            EmployeeNumber = _people[b.Value.PersonId].Value.EmployeeNumber,
            IsSuperseded = _breaks.Values.Any(c => c.Value.CorrectsBreakId == b.Value.Id),
            IsApproved = _approvals.Any(a => a.Shift == b.Value.Shift),
            Version = b.Version,
        });

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
        Origin = p.Value.Origin,
        ExternalSystem = p.Value.ExternalSystem,
        ExternalKey = p.Value.ExternalKey,
        ExternalVersion = p.Value.ExternalVersion,
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
