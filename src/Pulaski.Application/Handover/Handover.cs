using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Handover;
using Pulaski.Domain;

namespace Pulaski.Application.Handover;

/// <summary>What a handover entry is (#124, D-113). Values match <c>core.handover_entry.kind</c>.</summary>
public static class HandoverKinds
{
    /// <summary>What happened: kept with its shift.</summary>
    public const string Note = "note";

    /// <summary>Something the next shift must see to: carried from shift to shift until someone closes it.</summary>
    public const string OpenItem = "open_item";

    public const int MaxText = 1000;
}

/// <summary>A line as the handover writes to it.</summary>
public sealed record HandoverLine(Guid Id, Guid SiteId, string Code, bool CanRunJob, bool IsActive);

/// <summary>An entry as the commands see it.</summary>
public sealed record HandoverEntry(Guid Id, Guid SiteId, Guid LineId, string Kind, string Body, DateOnly ProductionDate, string? ShiftCode,
    DateTimeOffset WrittenAt, string WrittenBy, DateTimeOffset? ClosedAt = null, string? ClosedBy = null, string? ClosingNote = null);

/// <summary>Handover writes (#124), inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IHandoverRepository
{
    Task<HandoverLine?> GetLineAsync(Guid lineId, CancellationToken cancellationToken);

    Task<HandoverEntry?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task InsertAsync(HandoverEntry entry, CancellationToken cancellationToken);

    Task CloseAsync(HandoverEntry entry, CancellationToken cancellationToken);
}

/// <summary>Handover reads (#124).</summary>
public interface IHandoverReadStore
{
    /// <summary>The site's active lines that run jobs (or the one asked for), by code.</summary>
    Task<IReadOnlyList<(Guid Id, string Code, string? Name)>> ListLinesAsync(Guid siteId, Guid? lineId, CancellationToken cancellationToken);

    Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The lines' entries written on or after <paramref name="fromDate"/>, and every open item whenever written; oldest first.</summary>
    Task<IReadOnlyList<HandoverEntryDto>> ListEntriesAsync(IReadOnlyCollection<Guid> lineIds, DateOnly fromDate, CancellationToken cancellationToken);
}

/// <summary>Writes a note or an open item for a line, in the shift running on it now (#124).</summary>
public sealed record AddHandoverEntryCommand(Guid LineId, string Kind, string Body) : ICommand<CreatedResponse>;

/// <summary>Closes an open item, with what was done.</summary>
public sealed record CloseHandoverItemCommand(Guid Id, string? Note) : ICommand<VersionResponse>;

/// <summary>A site's handover: each line's open items and its current and previous shift (#124); one line when given.</summary>
public sealed record GetHandoverQuery(Guid SiteId, Guid? LineId = null) : IQuery<HandoverDto>;

internal sealed class AddHandoverEntryHandler(
    IUnitOfWork unitOfWork, IHandoverRepository handover, ILaborReadStore shifts, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<AddHandoverEntryCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(AddHandoverEntryCommand command, CancellationToken cancellationToken)
    {
        var body = HandoverText.Required(command.Body);
        if (command.Kind is not (HandoverKinds.Note or HandoverKinds.OpenItem))
        {
            throw new DomainRuleException("A handover entry is a note or an open item.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var line = await handover.GetLineAsync(command.LineId, cancellationToken) ?? throw new NotFoundException($"Line {command.LineId} not found.");
        if (!line.CanRunJob || !line.IsActive)
        {
            throw new DomainRuleException($"{line.Code} {(line.IsActive ? "isn't a line" : "is switched off")}; write handover notes for a line.");
        }

        var envelope = events.Create(EventTypes.HandoverEntryAdded, "handover_entry", ids.NewId(), new { Line = line.Code, command.Kind, Body = body });
        var calendars = await shifts.ListEquipmentShiftsAsync([line.Id], cancellationToken);
        var calendar = calendars.Count > 0 ? calendars[0] : null;
        var (date, shift) = calendar?.ShiftAt(envelope.OccurredAt) ?? (DateOnly.FromDateTime(envelope.OccurredAt.UtcDateTime), null);
        await handover.InsertAsync(
            new HandoverEntry(envelope.Subject, line.SiteId, line.Id, command.Kind, body, date, shift?.Code, envelope.OccurredAt, envelope.ActorId),
            cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(envelope.Subject, 0);
    }
}

internal sealed class CloseHandoverItemHandler(IUnitOfWork unitOfWork, IHandoverRepository handover, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<CloseHandoverItemCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(CloseHandoverItemCommand command, CancellationToken cancellationToken)
    {
        var note = HandoverText.Optional(command.Note);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var entry = await handover.GetForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Handover entry {command.Id} not found.");
        if (entry.Kind != HandoverKinds.OpenItem)
        {
            throw new DomainRuleException("Only an open item is closed; a note stays as it was written.");
        }

        if (entry.ClosedAt is not null)
        {
            throw new DomainRuleException("This item is closed already.");
        }

        var envelope = events.Create(EventTypes.HandoverItemClosed, "handover_entry", entry.Id, new { Note = note });
        await handover.CloseAsync(entry with { ClosedAt = envelope.OccurredAt, ClosedBy = envelope.ActorId, ClosingNote = note }, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

internal sealed class GetHandoverHandler(IHandoverReadStore store, ILaborReadStore shifts, TimeProvider timeProvider) : IQueryHandler<GetHandoverQuery, HandoverDto>
{
    public async Task<HandoverDto> HandleAsync(GetHandoverQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lines = await store.ListLinesAsync(query.SiteId, query.LineId, cancellationToken);
        var zoneId = await store.GetSiteTimeZoneAsync(query.SiteId, cancellationToken);
        if (lines.Count == 0)
        {
            return new HandoverDto { SiteId = query.SiteId, TimeZone = zoneId };
        }

        var calendars = (await shifts.ListEquipmentShiftsAsync([.. lines.Select(l => l.Id)], cancellationToken)).ToDictionary(c => c.EquipmentId);
        var shiftsByLine = lines.ToDictionary(l => l.Id, l => HandoverShifts.CurrentAndPrevious(calendars.GetValueOrDefault(l.Id), now));
        var from = shiftsByLine.Values.Min(s => s.Previous.ProductionDate);
        var entries = await store.ListEntriesAsync([.. lines.Select(l => l.Id)], from, cancellationToken);

        return new HandoverDto
        {
            SiteId = query.SiteId,
            TimeZone = zoneId,
            Lines = [.. lines.Select(l =>
            {
                var (current, previous) = shiftsByLine[l.Id];
                var own = entries.Where(e => e.LineId == l.Id).ToList();
                return new HandoverLineDto
                {
                    LineId = l.Id,
                    LineCode = l.Code,
                    LineName = l.Name,
                    Current = current with { Entries = [.. own.Where(e => HandoverShifts.Is(e, current))] },
                    Previous = previous with { Entries = [.. own.Where(e => HandoverShifts.Is(e, previous))] },
                    OpenItems = [.. own.Where(e => e.Kind == HandoverKinds.OpenItem && e.ClosedAt is null)],
                };
            })],
        };
    }
}

/// <summary>Which shift is now and which came before (#124). Pure.</summary>
public static class HandoverShifts
{
    /// <summary>
    /// The shift running on the line now and the one before it, by its schedule (D-038); between shifts, the one that ended last is the previous
    /// and the next one current. Without a schedule, today and yesterday (site-local).
    /// </summary>
    public static (HandoverShiftDto Current, HandoverShiftDto Previous) CurrentAndPrevious(EquipmentShifts? calendar, DateTimeOffset now)
    {
        if (calendar?.Schedule is null)
        {
            var zone = calendar?.Zone ?? TimeZoneInfo.Utc;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            return (new HandoverShiftDto { ProductionDate = today }, new HandoverShiftDto { ProductionDate = today.AddDays(-1) });
        }

        // The shift now, else the next one to start (looking a day ahead); the previous one ends at or before it starts.
        var (date, current) = calendar.ShiftAt(now);
        current ??= Enumerable.Range(1, 48).Select(h => calendar.ShiftAt(now.AddMinutes(30 * h)).Shift).FirstOrDefault(s => s is not null);
        if (current is null)
        {
            return (new HandoverShiftDto { ProductionDate = date }, new HandoverShiftDto { ProductionDate = date.AddDays(-1) });
        }

        var previous = Enumerable.Range(1, 96).Select(h => calendar.ShiftAt(current.Start.AddMinutes(-30 * h)).Shift)
            .FirstOrDefault(s => s is not null && s.End <= current.Start);
        return (Of(current), previous is null ? new HandoverShiftDto { ProductionDate = current.ProductionDate.AddDays(-1) } : Of(previous));
    }

    /// <summary>Whether the entry was written in that shift.</summary>
    public static bool Is(HandoverEntryDto entry, HandoverShiftDto shift) =>
        entry.ProductionDate == shift.ProductionDate && string.Equals(entry.ShiftCode, shift.ShiftCode, StringComparison.Ordinal);

    private static HandoverShiftDto Of(Domain.Shifts.ShiftOccurrence shift) => new()
    {
        ProductionDate = shift.ProductionDate,
        ShiftCode = shift.Code,
        ShiftName = shift.Name,
        Start = shift.Start,
        End = shift.End,
    };
}

internal static class HandoverText
{
    public static string Required(string? text) =>
        Optional(text) ?? throw ValidationException.Field("Body", "Write what the next shift should know.");

    public static string? Optional(string? text)
    {
        var trimmed = text?.Trim();
        if (trimmed is { Length: > HandoverKinds.MaxText })
        {
            throw ValidationException.Field("Body", $"At most {HandoverKinds.MaxText} characters.");
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
