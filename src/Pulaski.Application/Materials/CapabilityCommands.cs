using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Materials;

/// <summary>The material can run on the line over a period (D-020). Creates the line's run standard from the ideal rate when it has none (D-034).</summary>
public sealed record AddCapabilityCommand(
    Guid MaterialId,
    Guid EquipmentId,
    decimal IdealRatePerHour,
    Guid RateUomId,
    int? StandardChangeoverMinutes,
    decimal? StandardCrewSize,
    bool IsPreferred,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveTo) : ICommand<CapabilityResponse>;

/// <summary>New values for a capability; a new ideal rate recalculates the planning rate while it is still based on it.</summary>
public sealed record UpdateCapabilityCommand(
    Guid Id,
    long ExpectedVersion,
    decimal IdealRatePerHour,
    Guid RateUomId,
    int? StandardChangeoverMinutes,
    decimal? StandardCrewSize,
    bool IsPreferred,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo) : ICommand<CapabilityResponse>;

/// <summary>Stops a capability now. It is never deleted.</summary>
public sealed record EndCapabilityCommand(Guid Id, long ExpectedVersion) : ICommand<CapabilityResponse>;

/// <summary>What every capability change checks and writes (D-034).</summary>
internal sealed class CapabilityWrites(
    ICapabilityRepository capabilities,
    IMaterialAdminRepository materials,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids)
{
    public const int MaxChangeoverMinutes = 7 * 24 * 60;

    public static CapabilityChange Change(
        decimal rate, Guid rateUomId, int? changeoverMinutes, decimal? crewSize, bool isPreferred, DateTimeOffset from, DateTimeOffset? to)
    {
        new Validator()
            .Require(rate > 0, "IdealRatePerHour", "Must be greater than zero.")
            .Require(rateUomId != Guid.Empty, "RateUomId", "Required.")
            .Require(changeoverMinutes is null or (>= 0 and <= MaxChangeoverMinutes), "StandardChangeoverMinutes", "Between 0 and 10080 minutes (7 days), or blank.")
            .Require(crewSize is null or (> 0 and <= Capability.MaxCrewSize), "StandardCrewSize", "Greater than 0 and at most 9999.99, or blank.")
            .Require(to is null || to > from, "EffectiveTo", "Must be after the start.")
            .ThrowIfInvalid();
        return new CapabilityChange(
            rate, rateUomId, changeoverMinutes is { } m ? TimeSpan.FromMinutes(m) : null, crewSize, isPreferred, from, to);
    }

    /// <summary>The line, locked first (D-017), so the change serializes with the scheduling commands that check eligibility.</summary>
    public async Task<CapabilityLine> LockLineAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await capabilities.LockLineAsync(equipmentId, cancellationToken)
            ?? throw new NotFoundException($"Equipment {equipmentId} not found.");

    /// <summary>The material, locked after the line; its rate unit must convert to the material's base unit (D-026).</summary>
    public async Task<Material> MaterialForRateAsync(Guid materialId, Guid rateUomId, Guid? currentUomId, CancellationToken cancellationToken)
    {
        var material = (await materials.GetForUpdateAsync(materialId, cancellationToken)
            ?? throw new NotFoundException($"Material {materialId} not found.")).Value;
        if (rateUomId == currentUomId)
        {
            return material;
        }

        if (!await materials.IsUomActiveAsync(rateUomId, cancellationToken))
        {
            throw new DomainRuleException("Choose an active unit for the rate.");
        }

        var converter = UnitConversions.For(
            material.Id, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([material.Id], cancellationToken));
        if (!converter.CanConvert(rateUomId, material.BaseUomId))
        {
            throw new DomainRuleException(
                $"{converter.Code(rateUomId)} doesn't convert to {converter.Code(material.BaseUomId)}, the base unit of {material.Code}; " +
                $"choose another unit, or add a conversion for {material.Code} on the Units page.");
        }

        return material;
    }

    /// <summary>Two periods of the same material on the same line can't overlap, so eligibility always has one answer.</summary>
    public async Task EnsureNoOverlapAsync(Capability capability, string lineCode, string materialCode, CancellationToken cancellationToken)
    {
        var others = await capabilities.ListOnLineAsync(capability.EquipmentId, capability.MaterialId, cancellationToken);
        if (others.Any(o => o.Id != capability.Id && o.Overlaps(capability.EffectiveFrom, capability.EffectiveTo)))
        {
            throw new DomainRuleException(
                $"{lineCode} already has a capability for {materialCode} during that period, and periods can't overlap. " +
                "Start this one after the other ends, or end or change the other first.");
        }
    }

    /// <summary>
    /// The line's planning rate follows the capability's ideal rate (D-034): created from it when the line has
    /// none, recalculated while it is still based on it. A modified rate is never touched.
    /// </summary>
    /// <returns>A note saying what happened to the planning rate, or null when nothing did.</returns>
    public async Task<string?> FollowIdealRateAsync(Capability capability, string unitCode, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var current = await capabilities.GetRunStandardForUpdateAsync(capability.EquipmentId, capability.MaterialId, cancellationToken);
        var rate = $"{Qty(capability.IdealRatePerHour)} {unitCode}/h";
        if (current is null)
        {
            var created = RunStandard.Create(
                ids.NewId(), capability.EquipmentId, capability.MaterialId, capability.RateUomId, capability.IdealRatePerHour, CalculationBasis.IdealRate, at);
            var made = events.Create(EventTypes.RunStandardCreated, "run_standard", created.Id, new { After = created });
            await capabilities.InsertRunStandardAsync(created, made.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(made, cancellationToken);
            return $"Planning rate set to {rate}, from the ideal rate.";
        }

        var followed = current.Value.FollowIdealRate(capability.IdealRatePerHour, capability.RateUomId, at);
        if (followed == current.Value)
        {
            return current.Value.CalculationBasis == CalculationBasis.IdealRate
                ? null
                : "The planning rate is calculated from run history, so the ideal rate doesn't change it.";
        }

        var envelope = events.Create(EventTypes.RunStandardRecalculated, "run_standard", followed.Id, new { Before = current.Value, After = followed });
        await capabilities.UpdateRunStandardCalculationAsync(followed, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return followed.ModifiedRatePerHour is { } modified
            ? $"Calculated rate now {rate}; the modified rate of {Qty(modified)} {unitCode}/h stays in force."
            : $"Planning rate now {rate}.";
    }

    /// <summary>A note when open jobs for the material on the line just lost the capability they need to move or dispatch there.</summary>
    public async Task<string?> OpenJobsLeftAsync(Capability before, Capability after, string lineCode, string materialCode, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (!before.IsInEffect(at) || after.IsInEffect(at))
        {
            return null;
        }

        var open = await capabilities.CountOpenJobsAsync(after.EquipmentId, after.MaterialId, cancellationToken);
        return open == 0
            ? null
            : $"{open} open {(open == 1 ? "job" : "jobs")} for {materialCode} on {lineCode} stay{(open == 1 ? "s" : "")} there, but can't be moved to it or dispatched on it until it has a capability again.";
    }

    private static string Qty(decimal value) => value.ToString("#,0.######", CultureInfo.InvariantCulture);
}

internal sealed class AddCapabilityHandler(
    IUnitOfWork unitOfWork,
    ICapabilityRepository capabilities,
    IMaterialAdminRepository materials,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<AddCapabilityCommand, CapabilityResponse>
{
    public async Task<CapabilityResponse> HandleAsync(AddCapabilityCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var change = CapabilityWrites.Change(
            command.IdealRatePerHour, command.RateUomId, command.StandardChangeoverMinutes, command.StandardCrewSize, command.IsPreferred,
            command.EffectiveFrom ?? now, command.EffectiveTo);
        var writes = new CapabilityWrites(capabilities, materials, units, eventLog, events, ids);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var line = await writes.LockLineAsync(command.EquipmentId, cancellationToken);
        if (!line.CanRunJob || !line.IsActive)
        {
            throw new DomainRuleException(
                $"{line.Code} {(line.IsActive ? "doesn't run jobs" : "is inactive")}; a capability goes on an active line that runs jobs. A department gets its lines' capabilities.");
        }

        var material = await writes.MaterialForRateAsync(command.MaterialId, command.RateUomId, null, cancellationToken);
        if (!material.IsActive)
        {
            throw new DomainRuleException($"{material.Code} is inactive; switch it on before adding a line that can run it.");
        }

        var capability = Capability.Create(ids.NewId(), line.Id, material.Id, change);
        await writes.EnsureNoOverlapAsync(capability, line.Code, material.Code, cancellationToken);

        var envelope = events.Create(EventTypes.CapabilityAdded, "equipment_capability", capability.Id, new { After = capability });
        var version = await capabilities.InsertAsync(capability, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        var note = await writes.FollowIdealRateAsync(capability, await UnitCodeAsync(units, capability.RateUomId, cancellationToken), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CapabilityResponse(capability.Id, version, note is null ? [] : [note]);
    }

    internal static async Task<string> UnitCodeAsync(IUnitRepository units, Guid uomId, CancellationToken cancellationToken) =>
        (await units.GetUnitsAsync(cancellationToken)).FirstOrDefault(u => u.Id == uomId)?.Code ?? "";
}

internal sealed class UpdateCapabilityHandler(
    IUnitOfWork unitOfWork,
    ICapabilityRepository capabilities,
    IMaterialAdminRepository materials,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<UpdateCapabilityCommand, CapabilityResponse>
{
    public async Task<CapabilityResponse> HandleAsync(UpdateCapabilityCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var change = CapabilityWrites.Change(
            command.IdealRatePerHour, command.RateUomId, command.StandardChangeoverMinutes, command.StandardCrewSize, command.IsPreferred,
            command.EffectiveFrom, command.EffectiveTo);
        var writes = new CapabilityWrites(capabilities, materials, units, eventLog, events, ids);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (line, current) = await LockAsync(capabilities, writes, command.Id, command.ExpectedVersion, cancellationToken);
        var material = await writes.MaterialForRateAsync(current.Value.MaterialId, command.RateUomId, current.Value.RateUomId, cancellationToken);
        var updated = current.Value.Edit(change);
        if (updated == current.Value)
        {
            return new CapabilityResponse(updated.Id, current.Version, []);
        }

        await writes.EnsureNoOverlapAsync(updated, line.Code, material.Code, cancellationToken);
        var envelope = events.Create(EventTypes.CapabilityChanged, "equipment_capability", updated.Id, new { Before = current.Value, After = updated });
        var version = await capabilities.UpdateAsync(updated, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        var notes = new List<string>();
        if (updated.IdealRatePerHour != current.Value.IdealRatePerHour || updated.RateUomId != current.Value.RateUomId)
        {
            notes.AddIfSet(await writes.FollowIdealRateAsync(
                updated, await AddCapabilityHandler.UnitCodeAsync(units, updated.RateUomId, cancellationToken), now, cancellationToken));
        }

        notes.AddIfSet(await writes.OpenJobsLeftAsync(current.Value, updated, line.Code, material.Code, now, cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        return new CapabilityResponse(updated.Id, version, notes);
    }

    /// <summary>The capability's line first, then the capability (D-017).</summary>
    internal static async Task<(CapabilityLine Line, Versioned<Capability> Capability)> LockAsync(
        ICapabilityRepository capabilities, CapabilityWrites writes, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var lineId = await capabilities.GetLineOfAsync(id, cancellationToken) ?? throw new NotFoundException($"Capability {id} not found.");
        var line = await writes.LockLineAsync(lineId, cancellationToken);
        var current = await capabilities.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Capability {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"The capability on {line.Code} was changed by someone else. Reload and try again.");
        }

        return (line, current);
    }
}

internal sealed class EndCapabilityHandler(
    IUnitOfWork unitOfWork,
    ICapabilityRepository capabilities,
    IMaterialAdminRepository materials,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<EndCapabilityCommand, CapabilityResponse>
{
    public async Task<CapabilityResponse> HandleAsync(EndCapabilityCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var writes = new CapabilityWrites(capabilities, materials, units, eventLog, events, ids);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (line, current) = await UpdateCapabilityHandler.LockAsync(capabilities, writes, command.Id, command.ExpectedVersion, cancellationToken);
        var ended = current.Value.End(now);
        var material = (await materials.GetForUpdateAsync(ended.MaterialId, cancellationToken))!.Value;

        var envelope = events.Create(EventTypes.CapabilityEnded, "equipment_capability", ended.Id, new { Before = current.Value, After = ended });
        var version = await capabilities.UpdateAsync(ended, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        var note = await writes.OpenJobsLeftAsync(current.Value, ended, line.Code, material.Code, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CapabilityResponse(ended.Id, version, note is null ? [] : [note]);
    }
}

internal static class NoteListExtensions
{
    public static void AddIfSet(this List<string> notes, string? note)
    {
        if (note is not null)
        {
            notes.Add(note);
        }
    }
}
