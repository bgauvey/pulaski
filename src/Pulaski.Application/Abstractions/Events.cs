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
    public const string EquipmentCalendarSet = "pulaski.equipment.calendar_set.v1";
    public const string ShiftScheduleCreated = "pulaski.shift_schedule.created.v1";
    public const string ShiftScheduleUpdated = "pulaski.shift_schedule.updated.v1";
    public const string WorkOrderCreated = "pulaski.work_order.created.v1";
    public const string WorkOrderStatusChanged = "pulaski.work_order.status_changed.v1";
    public const string JobCreated = "pulaski.job.created.v1";
    public const string JobSequenced = "pulaski.job.sequenced.v1";
    public const string JobPinned = "pulaski.job.pinned.v1";
    public const string JobAssigned = "pulaski.job.assigned.v1";
    public const string JobCancelled = "pulaski.job.cancelled.v1";
    public const string JobStarted = "pulaski.job.started.v1";
    public const string JobPaused = "pulaski.job.paused.v1";
    public const string JobCompleted = "pulaski.job.completed.v1";
    public const string JobReopened = "pulaski.job.reopened.v1";
    public const string JobClosed = "pulaski.job.closed.v1";
    public const string ProductionReported = "pulaski.job.production_reported.v1";
    public const string ExecutionPolicyChanged = "pulaski.execution_policy.changed.v1";
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
    public const string LotReceived = "pulaski.lot.received.v1";
    public const string LotWrittenUp = "pulaski.lot.written_up.v1";
    public const string LotWrittenOff = "pulaski.lot.written_off.v1";
    public const string LotMoved = "pulaski.lot.moved.v1";
    public const string LotStatusChanged = "pulaski.lot.status_changed.v1";
    public const string LotConsumed = "pulaski.lot.consumed.v1";
    public const string LotProduced = "pulaski.lot.produced.v1";
    public const string LotSplit = "pulaski.lot.split.v1";
    public const string LotMerged = "pulaski.lot.merged.v1";
    public const string LotShipped = "pulaski.lot.shipped.v1";
    public const string LotCorrected = "pulaski.lot.corrected.v1";
    public const string LotReversed = "pulaski.lot.reversed.v1";
    public const string LotCountAdjusted = "pulaski.lot.count_adjusted.v1";
    public const string StockCountStarted = "pulaski.stock_count.started.v1";
    public const string StockCountRecorded = "pulaski.stock_count.recorded.v1";
    public const string StockCountPosted = "pulaski.stock_count.posted.v1";
    public const string StockCountCancelled = "pulaski.stock_count.cancelled.v1";
    public const string ReasonCodeCreated = "pulaski.reason_code.created.v1";
    public const string ReasonCodeUpdated = "pulaski.reason_code.updated.v1";
    public const string MaterialShelfLifeSet = "pulaski.material.shelf_life_set.v1";
    public const string MaterialCreated = "pulaski.material.created.v1";
    public const string MaterialUpdated = "pulaski.material.updated.v1";
    public const string MaterialImported = "pulaski.material.imported.v1";
    public const string MaterialClassCreated = "pulaski.material_class.created.v1";
    public const string MaterialClassUpdated = "pulaski.material_class.updated.v1";
    public const string MaterialBatchingSet = "pulaski.material.batching_set.v1";
    public const string SignedIn = "pulaski.user.signed_in.v1";
    public const string SignInFailed = "pulaski.user.sign_in_failed.v1";
    public const string PasswordChanged = "pulaski.user.password_changed.v1";
    public const string PasswordReset = "pulaski.user.password_reset.v1";
    public const string UserCreated = "pulaski.user.created.v1";
    public const string UserUpdated = "pulaski.user.updated.v1";
    public const string RoleAssigned = "pulaski.user.role_assigned.v1";
    public const string RoleEnded = "pulaski.user.role_ended.v1";
    public const string RoleCreated = "pulaski.role.created.v1";
    public const string RoleUpdated = "pulaski.role.updated.v1";
    public const string RolePermissionChanged = "pulaski.role.permission_changed.v1";
    public const string PartialBatchReported = "pulaski.partial_batch.reported.v1";
    public const string PartialBatchApproved = "pulaski.partial_batch.approved.v1";
    public const string PartialBatchRejected = "pulaski.partial_batch.rejected.v1";
    public const string CapabilityAdded = "pulaski.capability.added.v1";
    public const string CapabilityChanged = "pulaski.capability.changed.v1";
    public const string CapabilityEnded = "pulaski.capability.ended.v1";
    public const string RunStandardCreated = "pulaski.run_standard.created.v1";
    public const string RunStandardRecalculated = "pulaski.run_standard.recalculated.v1";
}
