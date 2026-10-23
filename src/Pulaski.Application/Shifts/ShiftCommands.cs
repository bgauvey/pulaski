using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Shifts;

/// <summary>A new shift schedule at a site (D-038). Its code is fixed once created.</summary>
public sealed record CreateShiftScheduleCommand(Guid SiteId, string Code, string Name, string? Description, int CycleDays, DateOnly CycleStart, IReadOnlyList<ShiftDto> Shifts)
    : ICommand<CreatedResponse>;

/// <summary>Changes a schedule's shifts, cycle, name or active flag. Projected job times follow at once.</summary>
public sealed record UpdateShiftScheduleCommand(Guid Id, long ExpectedVersion, string Name, string? Description, int CycleDays, DateOnly CycleStart,
    IReadOnlyList<ShiftDto> Shifts, bool IsActive) : ICommand<VersionResponse>;

/// <summary>
/// Sets one date's override for a shift (D-042): it runs at other times, runs as an extra shift, or (not working) is off
/// that day. Start and length default to the cycle's shift of that code. The repeating cycle doesn't change.
/// </summary>
public sealed record SetShiftOverrideCommand(Guid ScheduleId, long ExpectedVersion, DateOnly Date, string Code, bool IsWorking, string? Name,
    string? Start, int? Minutes, string? Crew, string? Note) : ICommand<VersionResponse>;

/// <summary>Takes one date's override off a shift, so the cycle applies that day again (D-042).</summary>
public sealed record RemoveShiftOverrideCommand(Guid ScheduleId, long ExpectedVersion, DateOnly Date, string Code) : ICommand<VersionResponse>;

/// <summary>Which working time equipment follows (D-038): its parent's, its own schedule, or around the clock.</summary>
public sealed record SetEquipmentCalendarCommand(Guid EquipmentId, long ExpectedVersion, string Mode, Guid? ShiftScheduleId) : ICommand<VersionResponse>;

internal static class ShiftRules
{
    /// <summary>The crews the shifts name are active crews of the site (D-040).</summary>
    public static async Task EnsureCrewsAsync(IShiftRepository shifts, Guid siteId, IReadOnlyList<ShiftDto> definition, CancellationToken cancellationToken) =>
        await EnsureCrewsAsync(shifts, siteId, definition.Select(s => s.Crew), cancellationToken);

    public static async Task EnsureCrewsAsync(IShiftRepository shifts, Guid siteId, IEnumerable<string?> crews, CancellationToken cancellationToken)
    {
        var named = crews.OfType<string>().Distinct().ToList();
        if (named.Count == 0)
        {
            return;
        }

        var active = await shifts.ListCrewCodesAsync(siteId, cancellationToken);
        var unknown = named.Except(active, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Shifts"] = [$"Not an active crew of the site: {string.Join(", ", unknown)}."] });
        }
    }

    /// <summary>Checks the fields and the shifts; throws a validation error naming what is wrong.</summary>
    public static (string Name, string? Description, IReadOnlyList<ShiftDto> Shifts) Validate(
        Validator validator, string code, string name, string? description, int cycleDays, DateOnly cycleStart, IReadOnlyList<ShiftDto> shifts,
        IReadOnlyList<ShiftOverrideDto>? overrides = null)
    {
        var trimmedName = name.Trim();
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        validator
            .RequireText(trimmedName, "Name", 100)
            .Require(trimmedDescription is null || trimmedDescription.Length <= 500, "Description", "At most 500 characters.")
            .Require(cycleStart != default, "CycleStart", "Choose the cycle's first day.")
            .Require(shifts.All(ShiftMapping.HasValidStart), "Shifts", "Give each shift a start time as HH:mm.");
        validator.ThrowIfInvalid();

        ThrowIfProblems(ShiftMapping.ToDomain(code, trimmedName, cycleDays, cycleStart, shifts, overrides));
        return (trimmedName, trimmedDescription, ShiftMapping.Normalize(shifts));
    }

    public static void ThrowIfProblems(ShiftSchedule schedule)
    {
        var problems = schedule.Problems();
        if (problems.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Shifts"] = [.. problems] });
        }
    }

    /// <summary>The schedule, locked, at the version the caller saw.</summary>
    public static async Task<ShiftScheduleRecord> GetAtVersionAsync(IShiftRepository shifts, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await shifts.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Shift schedule {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }

    /// <summary>Approved labor locks its shift (D-040), so the shift's times on that day can't change under it.</summary>
    public static async Task EnsureNotApprovedAsync(IShiftRepository shifts, ShiftScheduleRecord schedule, DateOnly date, string code, CancellationToken cancellationToken)
    {
        if (await shifts.IsShiftApprovedAsync(schedule.SiteId, schedule.Id, date, code, cancellationToken))
        {
            throw new DomainRuleException($"Labor for shift {code} on {date:yyyy-MM-dd} is approved, so its times are locked.");
        }
    }
}

internal sealed class SetShiftOverrideHandler(
    IUnitOfWork unitOfWork,
    IShiftRepository shifts,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetShiftOverrideCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetShiftOverrideCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim();
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        new Validator()
            .Require(command.Date != default, "Date", "Choose the day.")
            .Require(code.Length is > 0 and <= 10, "Code", "A shift code of up to 10 characters.")
            .Require(command.Start is null || ShiftMapping.HasValidStart(new ShiftDto { Start = command.Start }), "Start", "A start time as HH:mm.")
            .Require(note is null || note.Length <= 500, "Note", "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ShiftRules.GetAtVersionAsync(shifts, command.ScheduleId, command.ExpectedVersion, cancellationToken);
        var schedule = current.ToDomain();

        // Times default to the cycle's shift of that code on that day, or of that code on any day for an extra shift.
        var regular = schedule.RegularShift(command.Date, code);
        var template = regular ?? schedule.Shifts.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
        code = template?.Code ?? code;
        if (!command.IsWorking && regular is null)
        {
            throw new DomainRuleException($"Shift {code} doesn't run on {command.Date:yyyy-MM-dd}, so there is nothing to take off.");
        }

        var start = command.Start ?? template?.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        var minutes = command.Minutes ?? (int?)template?.Length.TotalMinutes;
        if (command.IsWorking && (start is null || minutes is null))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Start"] = [$"{code} isn't one of the schedule's shifts; give its start and length."] });
        }

        var crew = command.IsWorking ? (string.IsNullOrWhiteSpace(command.Crew) ? null : command.Crew.Trim().ToUpperInvariant()) : null;
        var entry = new ShiftOverrideDto
        {
            Date = command.Date,
            Code = code,
            IsWorking = command.IsWorking,
            Name = string.IsNullOrWhiteSpace(command.Name) ? template?.Name ?? code : command.Name.Trim(),
            Start = start ?? "00:00",
            Minutes = minutes ?? 0,
            Crew = crew,
            Note = note,
        };

        var before = current.Overrides?.FirstOrDefault(x => x.Date == command.Date && string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        if (before == entry)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        await ShiftRules.EnsureNotApprovedAsync(shifts, current, command.Date, code, cancellationToken);
        await ShiftRules.EnsureCrewsAsync(shifts, current.SiteId, [crew], cancellationToken);
        var changed = schedule.WithOverride(command.Date, code, ShiftMapping.ToDomain(entry));
        ShiftRules.ThrowIfProblems(changed);

        var updated = current with { Overrides = ShiftMapping.Sort([.. changed.Overrides!.Select(ShiftMapping.ToDto)]) };
        var envelope = events.Create(EventTypes.ShiftOverrideSet, "shift_schedule", current.Id, new { Before = before, After = entry });
        var version = await shifts.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class RemoveShiftOverrideHandler(
    IUnitOfWork unitOfWork,
    IShiftRepository shifts,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<RemoveShiftOverrideCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RemoveShiftOverrideCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ShiftRules.GetAtVersionAsync(shifts, command.ScheduleId, command.ExpectedVersion, cancellationToken);
        var code = command.Code.Trim();
        var before = current.Overrides?.FirstOrDefault(x => x.Date == command.Date && string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException($"{current.Code} has no override for shift {code} on {command.Date:yyyy-MM-dd}.");

        await ShiftRules.EnsureNotApprovedAsync(shifts, current, command.Date, before.Code, cancellationToken);
        var changed = current.ToDomain().WithOverride(command.Date, before.Code, null);
        ShiftRules.ThrowIfProblems(changed);

        var updated = current with { Overrides = ShiftMapping.Sort([.. changed.Overrides!.Select(ShiftMapping.ToDto)]) };
        var envelope = events.Create(EventTypes.ShiftOverrideRemoved, "shift_schedule", current.Id, new { Before = before });
        var version = await shifts.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class CreateShiftScheduleHandler(
    IUnitOfWork unitOfWork,
    IShiftRepository shifts,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateShiftScheduleCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateShiftScheduleCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var validator = new Validator().RequireText(code, "Code", ShiftSchedule.MaxCodeLength);
        var (name, description, definition) = ShiftRules.Validate(
            validator, code, command.Name, command.Description, command.CycleDays, command.CycleStart, command.Shifts);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await shifts.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("A shift schedule belongs to a site; choose one.");
        }

        if (await shifts.CodeExistsAsync(command.SiteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has a shift schedule {code}.");
        }

        await ShiftRules.EnsureCrewsAsync(shifts, command.SiteId, definition, cancellationToken);

        var schedule = new ShiftScheduleRecord(ids.NewId(), command.SiteId, code, name, description, command.CycleDays, command.CycleStart, definition, IsActive: true);
        var envelope = events.Create(EventTypes.ShiftScheduleCreated, "shift_schedule", schedule.Id, new { After = schedule });
        var version = await shifts.InsertAsync(schedule, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(schedule.Id, version);
    }
}

internal sealed class UpdateShiftScheduleHandler(
    IUnitOfWork unitOfWork,
    IShiftRepository shifts,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateShiftScheduleCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateShiftScheduleCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await shifts.GetForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Shift schedule {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var (name, description, definition) = ShiftRules.Validate(
            new Validator(), current.Value.Code, command.Name, command.Description, command.CycleDays, command.CycleStart, command.Shifts, current.Value.Overrides);
        await ShiftRules.EnsureCrewsAsync(shifts, current.Value.SiteId, definition, cancellationToken);

        // Equipment that follows a schedule needs it: switch the equipment to another first (it is never left without working time).
        if (current.Value.IsActive && !command.IsActive && await shifts.ListUsersAsync(current.Value.Id, cancellationToken) is { Count: > 0 } users)
        {
            throw new DomainRuleException($"{string.Join(", ", users)} {(users.Count == 1 ? "follows" : "follow")} {current.Value.Code}; give {(users.Count == 1 ? "it" : "them")} another schedule first.");
        }

        var updated = current.Value with
        {
            Name = name,
            Description = description,
            CycleDays = command.CycleDays,
            CycleStart = command.CycleStart,
            Shifts = definition,
            IsActive = command.IsActive,
        };
        if (updated.Name == current.Value.Name && updated.Description == current.Value.Description && updated.CycleDays == current.Value.CycleDays
            && updated.CycleStart == current.Value.CycleStart && updated.IsActive == current.Value.IsActive
            && updated.Shifts.SequenceEqual(current.Value.Shifts, ShiftComparer.Instance))
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.ShiftScheduleUpdated, "shift_schedule", updated.Id, new { Before = current.Value, After = updated });
        var version = await shifts.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }

    private sealed class ShiftComparer : IEqualityComparer<ShiftDto>
    {
        public static readonly ShiftComparer Instance = new();

        public bool Equals(ShiftDto? x, ShiftDto? y) =>
            x is not null && y is not null && x.Code == y.Code && x.Name == y.Name && x.Start == y.Start && x.Minutes == y.Minutes && x.Days.SequenceEqual(y.Days) && x.Crew == y.Crew;

        public int GetHashCode(ShiftDto obj) => HashCode.Combine(obj.Code, obj.Start, obj.Minutes);
    }
}

internal sealed class SetEquipmentCalendarHandler(
    IUnitOfWork unitOfWork,
    IShiftRepository shifts,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetEquipmentCalendarCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetEquipmentCalendarCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(CalendarModes.All.Contains(command.Mode), "Mode", "Inherit, schedule or around the clock.")
            .Require(command.Mode == CalendarModes.Schedule || command.ShiftScheduleId is null, "ShiftScheduleId", "Only a schedule names one.")
            .Require(command.Mode != CalendarModes.Schedule || command.ShiftScheduleId is not null, "ShiftScheduleId", "Choose a schedule.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await shifts.GetEquipmentCalendarForUpdateAsync(command.EquipmentId, cancellationToken)
            ?? throw new NotFoundException($"Equipment {command.EquipmentId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        if (command.ShiftScheduleId is { } scheduleId)
        {
            if (current.Value.SiteId is null)
            {
                throw new DomainRuleException($"{current.Value.Code} is above the sites; schedules belong to a site, so choose them for the sites or below.");
            }

            var schedule = await shifts.GetForShareAsync(scheduleId, cancellationToken) ?? throw new DomainRuleException("That shift schedule doesn't exist.");
            if (schedule.SiteId != current.Value.SiteId)
            {
                throw new DomainRuleException($"{schedule.Code} belongs to another site; {current.Value.Code} can only follow its own site's schedules.");
            }

            if (!schedule.IsActive)
            {
                throw new DomainRuleException($"{schedule.Code} is switched off.");
            }
        }

        if (current.Value.Mode == command.Mode && current.Value.ShiftScheduleId == command.ShiftScheduleId)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.EquipmentCalendarSet, "equipment", current.Value.Id, new
        {
            Before = new { current.Value.Mode, current.Value.ShiftScheduleId },
            After = new { command.Mode, command.ShiftScheduleId },
        });
        var version = await shifts.SetEquipmentCalendarAsync(current.Value.Id, command.Mode, command.ShiftScheduleId, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
