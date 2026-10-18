using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Shifts;

/// <summary>A shift schedule as the commands see it (D-038).</summary>
public sealed record ShiftScheduleRecord(Guid Id, Guid SiteId, string Code, string Name, string? Description, int CycleDays, DateOnly CycleStart,
    IReadOnlyList<ShiftDto> Shifts, bool IsActive);

/// <summary>The working-time setting of one piece of equipment (D-038).</summary>
public sealed record EquipmentCalendar(Guid Id, Guid? SiteId, string Code, string Level, string Mode, Guid? ShiftScheduleId);

/// <summary>Read side for shift schedules (D-038).</summary>
public interface IShiftReadStore
{
    /// <summary>Schedules of one site, or of every site; inactive ones too.</summary>
    Task<IReadOnlyList<ShiftScheduleDto>> ListAsync(Guid? siteId, CancellationToken cancellationToken);

    Task<ShiftScheduleDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The site's IANA time zone; null when there is no such site.</summary>
    Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IShiftRepository
{
    Task<Versioned<ShiftScheduleRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The schedule, locked against being switched off while equipment is given it.</summary>
    Task<ShiftScheduleRecord?> GetForShareAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The codes of the site's active crews (D-040), for checking the crews shifts name.</summary>
    Task<IReadOnlyList<string>> ListCrewCodesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<long> InsertAsync(ShiftScheduleRecord schedule, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(ShiftScheduleRecord schedule, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Codes of the equipment that has the schedule as its own.</summary>
    Task<IReadOnlyList<string>> ListUsersAsync(Guid scheduleId, CancellationToken cancellationToken);

    Task<Versioned<EquipmentCalendar>?> GetEquipmentCalendarForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task<long> SetEquipmentCalendarAsync(Guid equipmentId, string mode, Guid? shiftScheduleId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Between the shifts as the API carries them and the domain's schedule.</summary>
public static class ShiftMapping
{
    public static ShiftSchedule ToDomain(string code, string name, int cycleDays, DateOnly cycleStart, IReadOnlyList<ShiftDto> shifts) =>
        new(code, name, cycleDays, cycleStart, [.. shifts.Select(ToDomain)]);

    public static ShiftDefinition ToDomain(ShiftDto shift) => new(
        shift.Code.Trim(),
        string.IsNullOrWhiteSpace(shift.Name) ? shift.Code.Trim() : shift.Name.Trim(),
        TimeOnly.TryParseExact(shift.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ? start : TimeOnly.MinValue,
        TimeSpan.FromMinutes(shift.Minutes),
        [.. shift.Days.Distinct().Order()],
        string.IsNullOrWhiteSpace(shift.Crew) ? null : shift.Crew.Trim().ToUpperInvariant());

    /// <summary>The shifts as stored: trimmed, days sorted, each shift's name defaulting to its code.</summary>
    public static IReadOnlyList<ShiftDto> Normalize(IReadOnlyList<ShiftDto> shifts) =>
        [.. shifts.Select(s => ToDomain(s)).Select(s => new ShiftDto
        {
            Code = s.Code,
            Name = s.Name,
            Start = s.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
            Minutes = (int)s.Length.TotalMinutes,
            Days = s.Days,
            Crew = s.Crew,
        })];

    public static bool HasValidStart(ShiftDto shift) =>
        TimeOnly.TryParseExact(shift.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static decimal Hours(TimeSpan time) => Math.Round((decimal)time.TotalHours, 2);
}
