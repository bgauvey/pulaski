using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Scheduling;

/// <summary>Sets the gold-standard rate on a run standard (D-016). The actor is recorded as <c>modified_by</c>.</summary>
public sealed record ModifyRunStandardCommand(Guid Id, long ExpectedVersion, decimal RatePerHour, string? Note) : ICommand<VersionResponse>;

/// <summary>Drops the modified rate, so the calculated rate is in force again (D-016).</summary>
public sealed record ClearRunStandardCommand(Guid Id, long ExpectedVersion, string? Reason) : ICommand<VersionResponse>;

/// <summary>Sets a site's changeover mode (D-015). Version 0 means the site has no policy row yet.</summary>
public sealed record SetSchedulingPolicyCommand(Guid SiteId, long ExpectedVersion, string ChangeoverMode) : ICommand<VersionResponse>;

internal sealed class ModifyRunStandardHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    IRequestContext request,
    TimeProvider timeProvider) : ICommandHandler<ModifyRunStandardCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ModifyRunStandardCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.RatePerHour > 0, nameof(command.RatePerHour), "Must be greater than zero.")
            .Require(command.Note is null || command.Note.Length <= 500, nameof(command.Note), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var current = await RunStandards.LockAsync(scheduling, command.Id, command.ExpectedVersion, cancellationToken);
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        var modified = current.Modify(command.RatePerHour, request.ActorId, note, timeProvider.GetUtcNow());

        var envelope = events.Create(
            EventTypes.RunStandardModified, "run_standard", modified.Id, RunStandards.Change(current, modified));
        var version = await scheduling.UpdateRunStandardModificationAsync(modified, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class ClearRunStandardHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<ClearRunStandardCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ClearRunStandardCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Reason is null || command.Reason.Length <= 500, nameof(command.Reason), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var current = await RunStandards.LockAsync(scheduling, command.Id, command.ExpectedVersion, cancellationToken);
        var cleared = current.ClearModification();

        var envelope = events.Create(
            EventTypes.RunStandardModificationCleared,
            "run_standard",
            cleared.Id,
            RunStandards.Change(current, cleared),
            command.Reason);
        var version = await scheduling.UpdateRunStandardModificationAsync(cleared, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class SetSchedulingPolicyHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IEquipmentRepository equipment,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetSchedulingPolicyCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetSchedulingPolicyCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(
                ChangeoverMode.All.Contains(command.ChangeoverMode),
                nameof(command.ChangeoverMode),
                $"Unknown changeover mode '{command.ChangeoverMode}'.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The policy's events share the site's subject, so hold the site against concurrent edits.
        var site = await equipment.GetAsync(command.SiteId, RowLock.Share, cancellationToken)
            ?? throw new NotFoundException($"Site {command.SiteId} not found.");
        if (site.Value.Level != EquipmentTypes.Site)
        {
            throw new DomainRuleException($"{site.Value.Code} is not a site; scheduling policy is set per site.");
        }

        var current = await scheduling.GetPolicyForUpdateAsync(command.SiteId, cancellationToken);
        if ((current?.Version ?? 0) != command.ExpectedVersion)
        {
            throw new ConflictException($"The scheduling policy for {site.Value.Code} was changed by someone else. Reload and try again.");
        }

        var envelope = events.Create(
            EventTypes.SchedulingPolicyChanged,
            "equipment",
            command.SiteId,
            new
            {
                Before = new { ChangeoverMode = current?.Value ?? ChangeoverMode.Auto },
                After = new { command.ChangeoverMode },
            });
        var version = current is null
            ? await scheduling.InsertPolicyAsync(command.SiteId, command.ChangeoverMode, envelope.OccurredAt, cancellationToken)
            : await scheduling.UpdatePolicyAsync(command.SiteId, command.ChangeoverMode, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal static class RunStandards
{
    public static async Task<RunStandard> LockAsync(
        ISchedulingRepository scheduling, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await scheduling.GetRunStandardForUpdateAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Run standard {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException("The run standard was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }

    /// <summary>Event payload: the modified-rate fields before and after, and which rate is now in force.</summary>
    public static object Change(RunStandard before, RunStandard after) => new
    {
        before.EquipmentId,
        before.MaterialId,
        Before = Modification(before),
        After = Modification(after),
    };

    private static object Modification(RunStandard s) => new
    {
        s.ModifiedRatePerHour,
        s.ModifiedBy,
        s.ModifiedNote,
        s.RateSource,
        s.EffectiveRatePerHour,
    };
}
