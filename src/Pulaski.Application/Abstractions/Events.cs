namespace Pulaski.Application.Abstractions;

/// <summary>
/// A CloudEvents envelope as stored in <c>evt.event</c> (D-008). <see cref="Data"/> is serialized
/// to JSON; changes carry <c>before</c> and <c>after</c>.
/// </summary>
public sealed record EventEnvelope(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Type,
    string Source,
    Guid Subject,
    string SubjectType,
    string ActorType,
    string ActorId,
    Guid CorrelationId,
    Guid? CausationId,
    string? Reason,
    object Data);

/// <summary>Appends to the event log inside the current unit of work. Append-only: no update or delete.</summary>
public interface IEventLog
{
    Task AppendAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Builds envelopes stamped with a new UUIDv7, the clock, the actor and the correlation id.</summary>
public interface IEventFactory
{
    EventEnvelope Create(string type, string subjectType, Guid subject, object data, string? reason = null);
}

internal sealed class EventFactory(
    IIdGenerator ids,
    TimeProvider timeProvider,
    IRequestContext request) : IEventFactory
{
    /// <summary>CloudEvents <c>source</c> for changes made through the API.</summary>
    public const string Source = "pulaski-api";

    public EventEnvelope Create(string type, string subjectType, Guid subject, object data, string? reason = null) =>
        new(
            ids.NewId(),
            timeProvider.GetUtcNow(),
            type,
            Source,
            subject,
            subjectType,
            ActorType: "user",
            request.ActorId,
            request.CorrelationId,
            CausationId: null,
            reason,
            data);
}

/// <summary>Versioned CloudEvents types. Add a new version rather than changing a payload.</summary>
public static class EventTypes
{
    public const string EquipmentCreated = "pulaski.equipment.created.v1";
    public const string EquipmentUpdated = "pulaski.equipment.updated.v1";
    public const string WorkOrderCreated = "pulaski.work_order.created.v1";
    public const string WorkOrderStatusChanged = "pulaski.work_order.status_changed.v1";
}
