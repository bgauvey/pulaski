using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>An equipment's machine data (D-041): its profile, where its state comes from, and its micro-stop threshold.</summary>
public sealed record SetEquipmentMachineDataCommand(
    Guid EquipmentId, long ExpectedVersion, Guid? MachineProfileId, string StateDerivation, Guid? ConstraintEquipmentId, int MicroStopThresholdSeconds)
    : ICommand<VersionResponse>;

/// <summary>One machine's own reason for a stop code of its profile; inactive removes it.</summary>
public sealed record SetStopReasonOverrideCommand(Guid EquipmentId, long ExpectedVersion, int RawValue, Guid? ReasonCodeId, bool IsActive)
    : ICommand<VersionResponse>;

internal static class EquipmentMachineDataLocks
{
    public static async Task<EquipmentMachineDataRecord> LockAsync(IMachineDataRepository machines, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await machines.GetEquipmentForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Equipment {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }
}

internal sealed class SetEquipmentMachineDataHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetEquipmentMachineDataCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetEquipmentMachineDataCommand command, CancellationToken cancellationToken)
    {
        var constraint = command.StateDerivation == StateDerivations.ConstraintMachine;
        new Validator()
            .Require(StateDerivations.All.Contains(command.StateDerivation), "StateDerivation", "Own tags, series, parallel or constraint machine.")
            .Require(!constraint || command.ConstraintEquipmentId is not null, "ConstraintEquipmentId", "Choose the constraint machine.")
            .Require(constraint || command.ConstraintEquipmentId is null, "ConstraintEquipmentId", "Only a constraint-machine line names one.")
            .ThrowIfInvalid();
        var threshold = TimeSpan.FromSeconds(command.MicroStopThresholdSeconds);
        MachineProfileRules.EnsureMicroStop(threshold);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await EquipmentMachineDataLocks.LockAsync(machines, command.EquipmentId, command.ExpectedVersion, cancellationToken);
        var updated = current with
        {
            MachineProfileId = command.MachineProfileId,
            StateDerivation = command.StateDerivation,
            ConstraintEquipmentId = command.ConstraintEquipmentId,
            MicroStopThreshold = threshold,
        };
        if (updated == current)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        if (updated.MachineProfileId is { } profileId && profileId != current.MachineProfileId)
        {
            if (!current.TracksState)
            {
                throw new DomainRuleException($"{current.Code} doesn't track state; tick Tracks state before giving it a machine profile.");
            }

            var profile = await machines.GetProfileForShareAsync(profileId, cancellationToken);
            if (profile is null || profile.SiteId != current.SiteId || !profile.IsActive)
            {
                throw new DomainRuleException("Choose an active machine profile of the equipment's site.");
            }
        }

        if (updated.ConstraintEquipmentId is { } machineId && machineId != current.ConstraintEquipmentId)
        {
            var candidate = await machines.GetConstraintCandidateAsync(current.EquipmentId, machineId, cancellationToken);
            if (candidate is not { IsBelow: true, TracksState: true })
            {
                throw new DomainRuleException($"The constraint machine is equipment below {current.Code} that tracks state.");
            }
        }

        var envelope = events.Create(EventTypes.EquipmentMachineDataSet, "equipment", current.EquipmentId, new { Before = current, After = updated });
        var version = await machines.UpdateEquipmentMachineDataAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetStopReasonOverrideHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetStopReasonOverrideCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetStopReasonOverrideCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var equipment = await EquipmentMachineDataLocks.LockAsync(machines, command.EquipmentId, command.ExpectedVersion, cancellationToken);
        if (equipment.MachineProfileId is not { } profileId || equipment.SiteId is not { } siteId)
        {
            throw new DomainRuleException($"{equipment.Code} has no machine profile, so it has no stop codes to override.");
        }

        if (command.IsActive)
        {
            var codes = await machines.ListCodesAsync(profileId, cancellationToken);
            if (!codes.Any(c => c.Kind == StopCodeKinds.StopReason && c.RawValue == command.RawValue))
            {
                throw new DomainRuleException($"Stop code {command.RawValue} isn't in {equipment.Code}'s machine profile.");
            }

            await ProfileRules.EnsureReasonAsync(machines, siteId, command.ReasonCodeId, cancellationToken);
        }

        var change = new { equipment.EquipmentId, command.RawValue, command.ReasonCodeId, command.IsActive };
        var envelope = events.Create(EventTypes.EquipmentStopReasonOverrideSet, "equipment", equipment.EquipmentId, change);
        await machines.UpsertOverrideAsync(equipment.EquipmentId, command.RawValue, command.IsActive ? command.ReasonCodeId : null, command.IsActive, cancellationToken);
        var version = await machines.TouchEquipmentAsync(equipment.EquipmentId, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
