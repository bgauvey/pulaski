using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>A stop as adjusting it sees it (D-069): all it holds, so the stops replacing it carry it on.</summary>
public sealed record AdjustableStop(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, Guid EquipmentId, Guid? RunId, int PackMlState, int UnitMode, int? PlcStopReason,
    Guid? MappedReasonCodeId, Guid? OperatorReasonCodeId, string? Comment, string? CodedBy, Guid? CodedByUserId, DateTimeOffset? CodedAt,
    string Source, bool IsSuperseded, bool IsMicroStop = false, IReadOnlyList<Guid>? ReplacesIds = null, DateTimeOffset? AdjustedAt = null)
{
    public StopInterval Interval => new(Id, StartedAt, EndedAt);

    public StopKeyDto Key => new(Id, StartedAt);
}

/// <summary>A stop an adjustment writes: closed, never a micro-stop, saying where it came from and who made it why.</summary>
public sealed record NewStop(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset EndedAt, Guid EquipmentId, Guid? RunId, int PackMlState, int UnitMode, int? PlcStopReason,
    Guid? MappedReasonCodeId, Guid? OperatorReasonCodeId, string? Comment, string? CodedBy, Guid? CodedByUserId, DateTimeOffset? CodedAt,
    string Source, IReadOnlyList<Guid>? ReplacesIds, string AdjustedBy, Guid? AdjustedByUserId, DateTimeOffset AdjustedAt, string AdjustmentComment,
    bool IsMicroStop = false)
{
    public StopKeyDto Key => new(Id, StartedAt);

    /// <summary>A part or the merge of <paramref name="from"/>, keeping its reasons and coding.</summary>
    public static NewStop From(
        AdjustableStop from, Guid id, DateTimeOffset start, DateTimeOffset end, string source, IReadOnlyList<Guid> replaces, Adjuster by) =>
        new(id, start, end, from.EquipmentId, from.RunId, from.PackMlState, from.UnitMode, from.PlcStopReason, from.MappedReasonCodeId,
            from.OperatorReasonCodeId, from.Comment, from.CodedBy, from.CodedByUserId, from.CodedAt, source, replaces, by.Actor, by.UserId, by.At,
            by.Comment);
}

/// <summary>Who adjusts stops, when and why.</summary>
public sealed record Adjuster(string Actor, Guid? UserId, DateTimeOffset At, string Comment);

/// <summary>The equipment whose stops are adjusted.</summary>
public sealed record StopEquipment(Guid Id, string Code, bool TracksState);

/// <summary>Write side for adjusting stops (D-069). Equipment is locked before its stops, as machine data ingest locks it.</summary>
public interface IStopAdjustmentRepository
{
    /// <summary>Locks the equipment, so its stops change one adjustment or ingest at a time.</summary>
    Task<StopEquipment?> LockEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken);

    /// <summary>The equipment the stops belong to, unlocked, so it can be locked first.</summary>
    Task<IReadOnlyList<Guid>> EquipmentOfStopsAsync(IReadOnlyList<StopKeyDto> stops, CancellationToken cancellationToken);

    /// <summary>The stops found, locked in id order.</summary>
    Task<IReadOnlyList<AdjustableStop>> GetStopsForUpdateAsync(IReadOnlyList<StopKeyDto> stops, CancellationToken cancellationToken);

    /// <summary>The equipment's current (not superseded) stops overlapping <c>[start, until)</c>, open ones too.</summary>
    Task<IReadOnlyList<StopInterval>> ListCurrentStopsAsync(Guid equipmentId, DateTimeOffset start, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>The run on the equipment at the time, if one was.</summary>
    Task<Guid?> RunAtAsync(Guid equipmentId, DateTimeOffset at, CancellationToken cancellationToken);

    Task InsertAsync(NewStop newStop, CancellationToken cancellationToken);

    /// <summary>Marks the stops replaced; <paramref name="note"/> says why when nothing replaces them (removed, undone).</summary>
    Task SupersedeAsync(IReadOnlyList<StopKeyDto> stops, string supersededBy, DateTimeOffset at, string? note, CancellationToken cancellationToken);

    /// <summary>The stops one adjustment made (#68): those of the equipment replacing exactly <paramref name="replacesIds"/>, made at <paramref name="adjustedAt"/>, locked.</summary>
    Task<IReadOnlyList<AdjustableStop>> ListAdjustmentAsync(Guid equipmentId, IReadOnlyList<Guid> replacesIds, DateTimeOffset adjustedAt, CancellationToken cancellationToken);

    /// <summary>The equipment's stops with these ids, from 62 days before <paramref name="near"/> on, locked: the stops an adjustment replaced.</summary>
    Task<IReadOnlyList<AdjustableStop>> GetReplacedForUpdateAsync(Guid equipmentId, IReadOnlyList<Guid> ids, DateTimeOffset near, CancellationToken cancellationToken);
}

/// <summary>A stop entered by hand (D-069), for an outage the machine didn't report.</summary>
public sealed record EnterStopCommand(Guid EquipmentId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, Guid ReasonCodeId, string? Comment)
    : ICommand<StopsAdjustedResponse>;

/// <summary>Splits a closed stop in two at a time inside it (D-069).</summary>
public sealed record SplitStopCommand(Guid Id, DateTimeOffset StartedAt, DateTimeOffset At, string? Comment) : ICommand<StopsAdjustedResponse>;

/// <summary>Merges consecutive closed stops of one machine into one (D-069).</summary>
public sealed record MergeStopsCommand(IReadOnlyList<StopKeyDto> Stops, string? Comment) : ICommand<StopsAdjustedResponse>;

/// <summary>Gives a closed stop other times (#68): it is replaced by one with the new times and all it held.</summary>
public sealed record RetimeStopCommand(Guid Id, DateTimeOffset StartedAt, DateTimeOffset NewStartedAt, DateTimeOffset NewEndedAt, string? Comment)
    : ICommand<StopsAdjustedResponse>;

/// <summary>Removes a stop entered by hand in error (#68): it is kept as history, saying why, and nothing replaces it.</summary>
public sealed record VoidStopCommand(Guid Id, DateTimeOffset StartedAt, string? Comment) : ICommand<StopsAdjustedResponse>;

/// <summary>Undoes the split, merge or retime that made this stop (#68): its stops are replaced by copies of the ones it replaced.</summary>
public sealed record UndoStopAdjustmentCommand(Guid Id, DateTimeOffset StartedAt, string? Comment) : ICommand<StopsAdjustedResponse>;

internal static class StopAdjusting
{
    /// <summary>The adjustment's comment, trimmed; every adjustment needs one.</summary>
    public static string Comment(string? comment, Validator validator)
    {
        var trimmed = comment?.Trim() ?? "";
        validator
            .Require(trimmed.Length > 0, "Comment", "Say why.")
            .Require(trimmed.Length <= 1000, "Comment", "At most 1000 characters.");
        return trimmed;
    }

    public static async Task<StopEquipment> LockAsync(IStopAdjustmentRepository adjustments, Guid equipmentId, CancellationToken cancellationToken)
    {
        var equipment = await adjustments.LockEquipmentAsync(equipmentId, cancellationToken) ?? throw new NotFoundException($"Equipment {equipmentId} not found.");
        return equipment.TracksState ? equipment : throw new DomainRuleException($"{equipment.Code} doesn't track state, so it has no stops.");
    }

    /// <summary>Locks the stops' machine, then the stops; every one must be found and all on one machine.</summary>
    public static async Task<(StopEquipment Equipment, IReadOnlyList<AdjustableStop> Stops)> LockStopsAsync(
        IStopAdjustmentRepository adjustments, IReadOnlyList<StopKeyDto> keys, CancellationToken cancellationToken)
    {
        var equipmentIds = await adjustments.EquipmentOfStopsAsync(keys, cancellationToken);
        if (equipmentIds.Count > 1)
        {
            throw new DomainRuleException("Only stops of one machine can be merged.");
        }

        var equipment = equipmentIds.Count == 1
            ? await LockAsync(adjustments, equipmentIds[0], cancellationToken)
            : throw new NotFoundException("Stop not found.");
        var stops = await adjustments.GetStopsForUpdateAsync(keys, cancellationToken);
        return stops.Count == keys.Count && stops.All(s => s.EquipmentId == equipment.Id)
            ? (equipment, stops)
            : throw new NotFoundException("Stop not found.");
    }
}

internal sealed class EnterStopHandler(
    IUnitOfWork unitOfWork, IStopAdjustmentRepository adjustments, IStopRepository stops, IEventLog eventLog, IEventFactory events, IIdGenerator ids,
    IRequestContext request, TimeProvider time) : ICommandHandler<EnterStopCommand, StopsAdjustedResponse>
{
    public async Task<StopsAdjustedResponse> HandleAsync(EnterStopCommand command, CancellationToken cancellationToken)
    {
        var validator = new Validator()
            .Require(command.EquipmentId != Guid.Empty, "EquipmentId", "Choose the machine.")
            .Require(command.ReasonCodeId != Guid.Empty, "ReasonCodeId", "Choose a reason.");
        var comment = StopAdjusting.Comment(command.Comment, validator);
        validator.ThrowIfInvalid();

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var equipment = await StopAdjusting.LockAsync(adjustments, command.EquipmentId, cancellationToken);
        var current = await adjustments.ListCurrentStopsAsync(equipment.Id, command.StartedAt, command.EndedAt, cancellationToken);
        StopAdjustments.EnsureCanEnter(command.StartedAt, command.EndedAt, now, current);
        if (!(await stops.ListCodingReasonsAsync(equipment.Id, cancellationToken)).Any(r => r.Id == command.ReasonCodeId))
        {
            throw new DomainRuleException($"Choose one of the reasons offered for {equipment.Code}.");
        }

        // No state was reported for it: it records Undefined, and the reason it was given as its coding.
        var stop = new NewStop(ids.NewId(), command.StartedAt, command.EndedAt, equipment.Id,
            await adjustments.RunAtAsync(equipment.Id, command.StartedAt, cancellationToken), PackMlStates.Undefined, LiveStateRules.DefaultUnitMode, null,
            null, command.ReasonCodeId, null, request.ActorId, request.UserId, now, StopSources.Manual, null, request.ActorId, request.UserId, now, comment);
        await adjustments.InsertAsync(stop, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.DowntimeEntered, "equipment", equipment.Id, new
        {
            StopId = stop.Id,
            stop.StartedAt,
            stop.EndedAt,
            command.ReasonCodeId,
            stop.RunId,
            Comment = comment,
        }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StopsAdjustedResponse([stop.Key]);
    }
}

internal sealed class SplitStopHandler(
    IUnitOfWork unitOfWork, IStopAdjustmentRepository adjustments, IEventLog eventLog, IEventFactory events, IIdGenerator ids, IRequestContext request,
    TimeProvider time) : ICommandHandler<SplitStopCommand, StopsAdjustedResponse>
{
    public async Task<StopsAdjustedResponse> HandleAsync(SplitStopCommand command, CancellationToken cancellationToken)
    {
        var validator = new Validator();
        var comment = StopAdjusting.Comment(command.Comment, validator);
        validator.ThrowIfInvalid();

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (equipment, found) = await StopAdjusting.LockStopsAsync(adjustments, [new StopKeyDto(command.Id, command.StartedAt)], cancellationToken);
        var stop = found[0];
        StopAdjustments.EnsureCanSplit(stop.Interval, stop.IsSuperseded, command.At, now);

        var by = new Adjuster(request.ActorId, request.UserId, now, comment);
        NewStop[] parts =
        [
            NewStop.From(stop, ids.NewId(), stop.StartedAt, command.At, StopSources.Split, [stop.Id], by),
            NewStop.From(stop, ids.NewId(), command.At, stop.EndedAt!.Value, StopSources.Split, [stop.Id], by),
        ];
        await adjustments.SupersedeAsync([stop.Key], request.ActorId, now, null, cancellationToken);
        foreach (var part in parts)
        {
            await adjustments.InsertAsync(part, cancellationToken);
        }

        await eventLog.AppendAsync(events.Create(EventTypes.DowntimeSplit, "equipment", equipment.Id, new
        {
            Stop = stop.Key,
            stop.EndedAt,
            command.At,
            Parts = parts.Select(p => p.Key).ToList(),
            Comment = comment,
        }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StopsAdjustedResponse([.. parts.Select(p => p.Key)]);
    }
}

internal sealed class MergeStopsHandler(
    IUnitOfWork unitOfWork, IStopAdjustmentRepository adjustments, IEventLog eventLog, IEventFactory events, IIdGenerator ids, IRequestContext request,
    TimeProvider time) : ICommandHandler<MergeStopsCommand, StopsAdjustedResponse>
{
    public async Task<StopsAdjustedResponse> HandleAsync(MergeStopsCommand command, CancellationToken cancellationToken)
    {
        var keys = command.Stops.Distinct().ToList();
        var validator = new Validator()
            .Require(keys.Count >= 2, "Stops", "Choose at least two stops to merge.")
            .Require(keys.Count <= 100, "Stops", "At most 100 stops at a time.");
        var comment = StopAdjusting.Comment(command.Comment, validator);
        validator.ThrowIfInvalid();

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (equipment, found) = await StopAdjusting.LockStopsAsync(adjustments, keys, cancellationToken);
        var ordered = found.OrderBy(s => s.StartedAt).ToList();
        var current = await adjustments.ListCurrentStopsAsync(
            equipment.Id, ordered[0].StartedAt, ordered.Max(s => s.EndedAt ?? now), cancellationToken);
        var (start, end) = StopAdjustments.EnsureCanMerge([.. ordered.Select(s => (s.Interval, s.IsSuperseded))], current, now);

        var merged = NewStop.From(ordered[0], ids.NewId(), start, end, StopSources.Merge, [.. ordered.Select(s => s.Id)],
            new Adjuster(request.ActorId, request.UserId, now, comment));
        await adjustments.SupersedeAsync([.. ordered.Select(s => s.Key)], request.ActorId, now, null, cancellationToken);
        await adjustments.InsertAsync(merged, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.DowntimeMerged, "equipment", equipment.Id, new
        {
            Stops = ordered.Select(s => new { s.Id, s.StartedAt, s.EndedAt }).ToList(),
            Merged = merged.Key,
            merged.EndedAt,
            Comment = comment,
        }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StopsAdjustedResponse([merged.Key]);
    }
}

internal sealed class RetimeStopHandler(
    IUnitOfWork unitOfWork, IStopAdjustmentRepository adjustments, IEventLog eventLog, IEventFactory events, IIdGenerator ids, IRequestContext request,
    TimeProvider time) : ICommandHandler<RetimeStopCommand, StopsAdjustedResponse>
{
    public async Task<StopsAdjustedResponse> HandleAsync(RetimeStopCommand command, CancellationToken cancellationToken)
    {
        var validator = new Validator();
        var comment = StopAdjusting.Comment(command.Comment, validator);
        validator.ThrowIfInvalid();

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (equipment, found) = await StopAdjusting.LockStopsAsync(adjustments, [new StopKeyDto(command.Id, command.StartedAt)], cancellationToken);
        var stop = found[0];
        var from = command.NewStartedAt < stop.StartedAt ? command.NewStartedAt : stop.StartedAt;
        var until = command.NewEndedAt > (stop.EndedAt ?? now) ? command.NewEndedAt : stop.EndedAt ?? now;
        var current = await adjustments.ListCurrentStopsAsync(equipment.Id, from, until, cancellationToken);
        StopAdjustments.EnsureCanRetime(stop.Interval, stop.IsSuperseded, command.NewStartedAt, command.NewEndedAt, now, current);

        var retimed = NewStop.From(stop, ids.NewId(), command.NewStartedAt, command.NewEndedAt, StopSources.Retime, [stop.Id],
            new Adjuster(request.ActorId, request.UserId, now, comment));
        await adjustments.SupersedeAsync([stop.Key], request.ActorId, now, null, cancellationToken);
        await adjustments.InsertAsync(retimed, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.DowntimeRetimed, "equipment", equipment.Id, new
        {
            Stop = stop.Key,
            stop.EndedAt,
            Retimed = retimed.Key,
            RetimedEndedAt = retimed.EndedAt,
            Comment = comment,
        }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StopsAdjustedResponse([retimed.Key]);
    }
}

internal sealed class VoidStopHandler(IUnitOfWork unitOfWork, IStopAdjustmentRepository adjustments, IEventLog eventLog, IEventFactory events, IRequestContext request,
    TimeProvider time) : ICommandHandler<VoidStopCommand, StopsAdjustedResponse>
{
    public async Task<StopsAdjustedResponse> HandleAsync(VoidStopCommand command, CancellationToken cancellationToken)
    {
        var validator = new Validator();
        var comment = StopAdjusting.Comment(command.Comment, validator);
        validator.ThrowIfInvalid();

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (equipment, found) = await StopAdjusting.LockStopsAsync(adjustments, [new StopKeyDto(command.Id, command.StartedAt)], cancellationToken);
        var stop = found[0];
        StopAdjustments.EnsureCanVoid(stop.Interval, stop.Source, stop.IsSuperseded);

        // Removed, not deleted: it stays as history, saying why, and nothing replaces it.
        await adjustments.SupersedeAsync([stop.Key], request.ActorId, now, $"Removed: {comment}", cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.DowntimeVoided, "equipment", equipment.Id, new
        {
            Stop = stop.Key,
            stop.EndedAt,
            Comment = comment,
        }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StopsAdjustedResponse([]);
    }
}

internal sealed class UndoStopAdjustmentHandler(
    IUnitOfWork unitOfWork, IStopAdjustmentRepository adjustments, IEventLog eventLog, IEventFactory events, IIdGenerator ids, IRequestContext request,
    TimeProvider time) : ICommandHandler<UndoStopAdjustmentCommand, StopsAdjustedResponse>
{
    public async Task<StopsAdjustedResponse> HandleAsync(UndoStopAdjustmentCommand command, CancellationToken cancellationToken)
    {
        var validator = new Validator();
        var comment = StopAdjusting.Comment(command.Comment, validator);
        validator.ThrowIfInvalid();

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (equipment, found) = await StopAdjusting.LockStopsAsync(adjustments, [new StopKeyDto(command.Id, command.StartedAt)], cancellationToken);
        var stop = found[0];
        if (stop.ReplacesIds is not { Count: > 0 } replacedIds || stop.AdjustedAt is not { } adjustedAt)
        {
            throw new DomainRuleException(stop.Source == StopSources.Manual
                ? "A stop entered by hand is removed, not undone."
                : "This stop didn't come from an adjustment, so there is nothing to undo.");
        }

        // The adjustment's stops (both parts of a split) and the stops it replaced, all locked.
        var results = await adjustments.ListAdjustmentAsync(equipment.Id, replacedIds, adjustedAt, cancellationToken);
        var replaced = await adjustments.GetReplacedForUpdateAsync(equipment.Id, replacedIds, stop.StartedAt, cancellationToken);
        if (replaced.Count != replacedIds.Count)
        {
            throw new DomainRuleException("The stops it replaced are too old to restore.");
        }

        var from = replaced.Concat(results).Min(s => s.StartedAt);
        var until = replaced.Concat(results).Max(s => s.EndedAt ?? now);
        var current = await adjustments.ListCurrentStopsAsync(equipment.Id, from, until, cancellationToken);
        StopAdjustments.EnsureCanUndo(stop.Source, [.. results.Select(r => (r.Interval, r.IsSuperseded))], [.. replaced.Select(r => r.Interval)], current);

        // The replaced stops come back as copies, with all they held: history is only ever added to.
        var by = new Adjuster(request.ActorId, request.UserId, now, comment);
        var restored = replaced.OrderBy(r => r.StartedAt)
            .Select(r => NewStop.From(r, ids.NewId(), r.StartedAt, r.EndedAt!.Value, StopSources.Restore, [.. results.Select(x => x.Id)], by) with { IsMicroStop = r.IsMicroStop })
            .ToList();
        await adjustments.SupersedeAsync([.. results.Select(r => r.Key)], request.ActorId, now, $"Undone: {comment}", cancellationToken);
        foreach (var restoredStop in restored)
        {
            await adjustments.InsertAsync(restoredStop, cancellationToken);
        }

        await eventLog.AppendAsync(events.Create(EventTypes.DowntimeRestored, "equipment", equipment.Id, new
        {
            Undone = stop.Source,
            Stops = results.Select(r => r.Key).ToList(),
            Replaced = replaced.Select(r => r.Key).ToList(),
            Restored = restored.Select(r => r.Key).ToList(),
            Comment = comment,
        }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StopsAdjustedResponse([.. restored.Select(r => r.Key)]);
    }
}
