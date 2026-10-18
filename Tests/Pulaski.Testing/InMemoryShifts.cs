using Pulaski.Application.Abstractions;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain.Shifts;

namespace Pulaski.Testing;

/// <summary>In-memory shift schedules and the working time each piece of equipment follows (D-038).</summary>
public sealed class InMemoryShifts : IShiftReadStore, IShiftRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<ShiftScheduleRecord>> _schedules = [];
    private readonly Dictionary<Guid, Versioned<EquipmentCalendar>> _equipment = [];
    private readonly Dictionary<Guid, string> _sites = [];
    private long _next = 1;

    public void AddSite(Guid siteId, string timeZone = "America/Chicago")
    {
        lock (_lock)
        {
            _sites[siteId] = timeZone;
            _equipment[siteId] = new(new EquipmentCalendar(siteId, siteId, "SITE", "site", CalendarModes.Inherit, null), ++_next);
        }
    }

    public Guid AddEquipment(Guid siteId, string code, string level = "work_center")
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _equipment[id] = new(new EquipmentCalendar(id, siteId, code, level, CalendarModes.Inherit, null), ++_next);
        }

        return id;
    }

    public Versioned<ShiftScheduleRecord>? Schedule(Guid id)
    {
        lock (_lock)
        {
            return _schedules.GetValueOrDefault(id);
        }
    }

    public Versioned<EquipmentCalendar>? Calendar(Guid id)
    {
        lock (_lock)
        {
            return _equipment.GetValueOrDefault(id);
        }
    }

    public Task<IReadOnlyList<ShiftScheduleDto>> ListAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ShiftScheduleDto>>([.. _schedules.Values.Where(s => siteId is null || s.Value.SiteId == siteId).Select(ToDto)]);
        }
    }

    public Task<ShiftScheduleDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_schedules.TryGetValue(id, out var s) ? ToDto(s) : null);
        }
    }

    public Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sites.GetValueOrDefault(siteId));
        }
    }

    public Task<Versioned<ShiftScheduleRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Schedule(id));

    public Task<ShiftScheduleRecord?> GetForShareAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Schedule(id)?.Value);

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_schedules.Values.Any(s => s.Value.SiteId == siteId && s.Value.Code == code));
        }
    }

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sites.ContainsKey(siteId));
        }
    }

    public Task<long> InsertAsync(ShiftScheduleRecord schedule, DateTimeOffset at, CancellationToken cancellationToken) => Save(schedule);

    public Task<long> UpdateAsync(ShiftScheduleRecord schedule, DateTimeOffset at, CancellationToken cancellationToken) => Save(schedule);

    public Task<IReadOnlyList<string>> ListUsersAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _equipment.Values.Where(e => e.Value.ShiftScheduleId == scheduleId).Select(e => e.Value.Code).Order(StringComparer.Ordinal)]);
        }
    }

    public Task<Versioned<EquipmentCalendar>?> GetEquipmentCalendarForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Calendar(equipmentId));

    public Task<long> SetEquipmentCalendarAsync(Guid equipmentId, string mode, Guid? shiftScheduleId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _equipment[equipmentId] = new(_equipment[equipmentId].Value with { Mode = mode, ShiftScheduleId = shiftScheduleId }, version);
            return Task.FromResult(version);
        }
    }

    private Task<long> Save(ShiftScheduleRecord schedule)
    {
        lock (_lock)
        {
            var version = ++_next;
            _schedules[schedule.Id] = new(schedule, version);
            return Task.FromResult(version);
        }
    }

    private ShiftScheduleDto ToDto(Versioned<ShiftScheduleRecord> s) => new()
    {
        Id = s.Value.Id,
        SiteId = s.Value.SiteId,
        Code = s.Value.Code,
        Name = s.Value.Name,
        Description = s.Value.Description,
        CycleDays = s.Value.CycleDays,
        CycleStart = s.Value.CycleStart,
        Shifts = s.Value.Shifts,
        IsActive = s.Value.IsActive,
        HoursPerCycle = ShiftMapping.Hours(ShiftMapping.ToDomain(s.Value.Code, s.Value.Name, s.Value.CycleDays, s.Value.CycleStart, s.Value.Shifts).HoursPerCycle),
        UsedBy = [.. _equipment.Values.Where(e => e.Value.ShiftScheduleId == s.Value.Id).Select(e => e.Value.Code)],
        Version = s.Version,
    };
}
