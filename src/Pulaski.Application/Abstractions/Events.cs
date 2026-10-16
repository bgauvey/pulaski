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
    public const string JobCreated = "pulaski.job.created.v1";
    public const string JobSequenced = "pulaski.job.sequenced.v1";
    public const string JobPinned = "pulaski.job.pinned.v1";
    public const string JobAssigned = "pulaski.job.assigned.v1";
    public const string JobCancelled = "pulaski.job.cancelled.v1";
    public const string WorkOrderDispatched = "pulaski.work_order.dispatched.v1";
    public const string WorkOrderUndispatched = "pulaski.work_order.undispatched.v1";
    public const string WorkOrderFirmed = "pulaski.work_order.firmed.v1";
    public const string RunStandardModified = "pulaski.run_standard.modified.v1";
    public const string RunStandardModificationCleared = "pulaski.run_standard.modification_cleared.v1";
    public const string SchedulingPolicyChanged = "pulaski.scheduling_policy.changed.v1";
    public const string ProcessCreated = "pulaski.process.created.v1";
    public const string ProcessUpdated = "pulaski.process.updated.v1";
    public const string ProcessDraftSaved = "pulaski.process.draft_saved.v1";
    public const string ProcessDraftStarted = "pulaski.process.draft_started.v1";
    public const string ProcessVersionReleased = "pulaski.process.version_released.v1";
    public const string ProcessScopeSet = "pulaski.process.scope_set.v1";
    public const string MaterialDefaultProcessSet = "pulaski.material.default_process_set.v1";
    public const string BomCreated = "pulaski.bom.created.v1";
    public const string BomDraftSaved = "pulaski.bom.draft_saved.v1";
    public const string BomDraftStarted = "pulaski.bom.draft_started.v1";
    public const string BomVersionReleased = "pulaski.bom.version_released.v1";
    public const string UnitCreated = "pulaski.uom.created.v1";
    public const string UnitUpdated = "pulaski.uom.updated.v1";
    public const string MaterialConversionSet = "pulaski.material.conversion_set.v1";
    public const string MaterialConversionRemoved = "pulaski.material.conversion_removed.v1";
}
