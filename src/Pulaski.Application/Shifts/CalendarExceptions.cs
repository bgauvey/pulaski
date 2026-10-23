using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Shifts;

/// <summary>A calendar exception as the commands see it (D-043).</summary>
public sealed record CalendarExceptionRecord(Guid Id, Guid ScopeEquipmentId, string Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Note, bool IsActive);

/// <summary>Read side for calendar exceptions (D-043).</summary>
public interface ICalendarExceptionReadStore
{
    /// <summary>The site's exceptions on anything in it that end after <paramref name="from"/> (all of them without), by start; removed ones only when asked.</summary>
    Task<IReadOnlyList<CalendarExceptionDto>> ListAsync(Guid siteId, DateTimeOffset? from, bool includeRemoved, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ICalendarExceptionRepository
{
    Task<Versioned<CalendarExceptionRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertAsync(CalendarExceptionRecord exception, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(CalendarExceptionRecord exception, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>
/// Takes time out of the calendar of a site, area or line and everything below it (D-043): a holiday, a shutdown, a week
/// with no demand. Planning places no work in it, whatever shift schedule the equipment follows.
/// </summary>
public sealed record CreateCalendarExceptionCommand(Guid ScopeEquipmentId, string Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Note)
    : ICommand<CreatedResponse>;

/// <summary>Changes an exception; <c>IsActive</c> false removes it (rows are never deleted) and true puts it back.</summary>
public sealed record UpdateCalendarExceptionCommand(Guid Id, long ExpectedVersion, string Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Note, bool IsActive)
    : ICommand<VersionResponse>;

public sealed record ListCalendarExceptionsQuery(Guid SiteId, DateTimeOffset? From, bool IncludeRemoved) : IQuery<IReadOnlyList<CalendarExceptionDto>>;

internal static class CalendarExceptionRules
{
    /// <returns>The trimmed note.</returns>
    public static string? Validate(string kind, DateTimeOffset startsAt, DateTimeOffset endsAt, string? note)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        new Validator()
            .Require(CalendarExceptionKind.All.Contains(kind), "Kind", "Holiday, no demand, planned shutdown or other.")
            .Require(startsAt != default, "StartsAt", "Choose when it starts.")
            .Require(endsAt > startsAt, "EndsAt", "It ends after it starts.")
            .Require(endsAt - startsAt <= CalendarExceptionKind.MaxLength, "EndsAt", "At most 366 days; add another for longer.")
            .Require(trimmed is null || trimmed.Length <= CalendarExceptionKind.MaxNoteLength, "Note", "At most 500 characters.")
            .ThrowIfInvalid();
        return trimmed;
    }

    /// <summary>The equipment it applies to, held while the exception is saved: active, and in a site (the enterprise has no calendar).</summary>
    public static async Task<Equipment> ScopeAsync(IEquipmentRepository equipment, Guid id, CancellationToken cancellationToken)
    {
        var scope = await equipment.GetAsync(id, RowLock.Share, cancellationToken) ?? throw new DomainRuleException("That equipment doesn't exist.");
        if (scope.Value.SiteId is null)
        {
            throw new DomainRuleException($"{scope.Value.Code} is above the sites; calendar exceptions are set for a site or something in it.");
        }

        if (!scope.Value.IsActive)
        {
            throw new DomainRuleException($"{scope.Value.Code} is switched off.");
        }

        return scope.Value;
    }
}

internal sealed class CreateCalendarExceptionHandler(
    IUnitOfWork unitOfWork,
    ICalendarExceptionRepository exceptions,
    IEquipmentRepository equipment,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateCalendarExceptionCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateCalendarExceptionCommand command, CancellationToken cancellationToken)
    {
        var note = CalendarExceptionRules.Validate(command.Kind, command.StartsAt, command.EndsAt, command.Note);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await CalendarExceptionRules.ScopeAsync(equipment, command.ScopeEquipmentId, cancellationToken);

        var created = new CalendarExceptionRecord(
            ids.NewId(), command.ScopeEquipmentId, command.Kind, command.StartsAt.ToUniversalTime(), command.EndsAt.ToUniversalTime(), note, IsActive: true);
        var envelope = events.Create(EventTypes.CalendarExceptionCreated, "calendar_exception", created.Id, new { After = created });
        var version = await exceptions.InsertAsync(created, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(created.Id, version);
    }
}

internal sealed class UpdateCalendarExceptionHandler(
    IUnitOfWork unitOfWork,
    ICalendarExceptionRepository exceptions,
    IEquipmentRepository equipment,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateCalendarExceptionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateCalendarExceptionCommand command, CancellationToken cancellationToken)
    {
        var note = CalendarExceptionRules.Validate(command.Kind, command.StartsAt, command.EndsAt, command.Note);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await exceptions.GetForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Calendar exception {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("The calendar exception was changed by someone else. Reload and try again.");
        }

        var updated = current.Value with
        {
            Kind = command.Kind,
            StartsAt = command.StartsAt.ToUniversalTime(),
            EndsAt = command.EndsAt.ToUniversalTime(),
            Note = note,
            IsActive = command.IsActive,
        };
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        // Removing one needs nothing of its equipment; changing or restoring one needs it still in use.
        if (updated.IsActive)
        {
            await CalendarExceptionRules.ScopeAsync(equipment, updated.ScopeEquipmentId, cancellationToken);
        }

        var type = current.Value.IsActive && !updated.IsActive ? EventTypes.CalendarExceptionRemoved : EventTypes.CalendarExceptionUpdated;
        var envelope = events.Create(type, "calendar_exception", updated.Id, new { Before = current.Value, After = updated });
        var version = await exceptions.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class ListCalendarExceptionsHandler(ICalendarExceptionReadStore store)
    : IQueryHandler<ListCalendarExceptionsQuery, IReadOnlyList<CalendarExceptionDto>>
{
    public Task<IReadOnlyList<CalendarExceptionDto>> HandleAsync(ListCalendarExceptionsQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, query.From, query.IncludeRemoved, cancellationToken);
}
