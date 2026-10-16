using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Units;

/// <summary>A new unit (D-026). Every dimension but <c>other</c> needs a factor; <c>other</c> is for packaging and has none.</summary>
public sealed record CreateUnitCommand(string Code, string Name, string Dimension, decimal? ToBaseFactor) : ICommand<CreatedResponse>;

/// <summary>Renames a unit or switches it off. Its code, dimension and factor never change, since quantities are stored in it.</summary>
public sealed record UpdateUnitCommand(Guid Id, long ExpectedVersion, string Name, bool IsActive) : ICommand<VersionResponse>;

/// <summary>Sets one of a material's own conversions: 1 from = factor to (D-026). Changing an existing one updates its factor.</summary>
public sealed record SetMaterialConversionCommand(Guid MaterialId, Guid FromUomId, Guid ToUomId, decimal Factor) : ICommand<MaterialConversionDto>;

/// <summary>Switches off one of a material's conversions, given in either direction. True when one was removed.</summary>
public sealed record RemoveMaterialConversionCommand(Guid MaterialId, Guid FromUomId, Guid ToUomId) : ICommand<bool>;

public static class UnitDimensions
{
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = ["mass", "length", "count", "volume", "area", "time", Other];
}

internal sealed class CreateUnitHandler(
    IUnitOfWork unitOfWork,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateUnitCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateUnitCommand command, CancellationToken cancellationToken)
    {
        var packaging = command.Dimension == UnitDimensions.Other;
        new Validator()
            .RequireText(command.Code, nameof(command.Code), 20)
            .RequireText(command.Name, nameof(command.Name), 100)
            .Require(UnitDimensions.All.Contains(command.Dimension), nameof(command.Dimension), $"Unknown dimension '{command.Dimension}'.")
            .Require(packaging || command.ToBaseFactor is > 0, nameof(command.ToBaseFactor), "Required, and greater than zero.")
            .Require(!packaging || command.ToBaseFactor is null, nameof(command.ToBaseFactor), "A packaging unit has no standard factor; give it a conversion per material.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var unit = new UnitOfMeasure(ids.NewId(), command.Code.Trim(), command.Name.Trim(), command.Dimension, command.ToBaseFactor, IsActive: true);
        var envelope = events.Create(EventTypes.UnitCreated, "uom", unit.Id, new { After = unit });
        var version = await units.InsertAsync(unit, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(unit.Id, version);
    }
}

internal sealed class UpdateUnitHandler(
    IUnitOfWork unitOfWork,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateUnitCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateUnitCommand command, CancellationToken cancellationToken)
    {
        new Validator().RequireText(command.Name, nameof(command.Name), 100).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await units.GetForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Unit {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"Unit {current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var updated = current.Value with { Name = command.Name.Trim(), IsActive = command.IsActive };
        var envelope = events.Create(EventTypes.UnitUpdated, "uom", updated.Id, new { Before = current.Value, After = updated });
        var version = await units.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class SetMaterialConversionHandler(
    IUnitOfWork unitOfWork,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetMaterialConversionCommand, MaterialConversionDto>
{
    public async Task<MaterialConversionDto> HandleAsync(SetMaterialConversionCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.FromUomId != Guid.Empty, nameof(command.FromUomId), "Required.")
            .Require(command.ToUomId != Guid.Empty, nameof(command.ToUomId), "Required.")
            .Require(command.Factor > 0, nameof(command.Factor), "Must be greater than zero.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await units.LockMaterialAsync(command.MaterialId, cancellationToken))
        {
            throw new NotFoundException($"Material {command.MaterialId} not found.");
        }

        var all = await units.GetUnitsAsync(cancellationToken);
        var existing = await units.GetConversionsAsync([command.MaterialId], cancellationToken);

        // Stored one way only: setting the reverse of an existing pair replaces it.
        var others = existing
            .Where(c => !SamePair(c, command.FromUomId, command.ToUomId))
            .Select(c => c.ToDomain())
            .ToList();
        var conversion = new MaterialConversion(command.FromUomId, command.ToUomId, command.Factor);
        MaterialConversions.EnsureCanAdd(all, others, conversion);

        var envelope = events.Create(
            EventTypes.MaterialConversionSet, "material", command.MaterialId, new { After = conversion });
        foreach (var reverse in existing.Where(c => c.FromUomId == command.ToUomId && c.ToUomId == command.FromUomId))
        {
            await units.DeactivateConversionAsync(command.MaterialId, reverse.FromUomId, reverse.ToUomId, envelope.OccurredAt, cancellationToken);
        }

        await units.SetConversionAsync(command.MaterialId, conversion, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new MaterialConversionDto
        {
            MaterialId = command.MaterialId,
            FromUomId = conversion.FromUomId,
            FromUomCode = all.First(u => u.Id == conversion.FromUomId).Code,
            ToUomId = conversion.ToUomId,
            ToUomCode = all.First(u => u.Id == conversion.ToUomId).Code,
            Factor = conversion.Factor,
            UpdatedAt = envelope.OccurredAt,
        };
    }

    private static bool SamePair(MaterialConversionRow row, Guid a, Guid b) =>
        (row.FromUomId == a && row.ToUomId == b) || (row.FromUomId == b && row.ToUomId == a);
}

internal sealed class RemoveMaterialConversionHandler(
    IUnitOfWork unitOfWork,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<RemoveMaterialConversionCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveMaterialConversionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await units.LockMaterialAsync(command.MaterialId, cancellationToken))
        {
            throw new NotFoundException($"Material {command.MaterialId} not found.");
        }

        var envelope = events.Create(
            EventTypes.MaterialConversionRemoved, "material", command.MaterialId, new { command.FromUomId, command.ToUomId });
        var removed = await units.DeactivateConversionAsync(command.MaterialId, command.FromUomId, command.ToUomId, envelope.OccurredAt, cancellationToken)
            | await units.DeactivateConversionAsync(command.MaterialId, command.ToUomId, command.FromUomId, envelope.OccurredAt, cancellationToken);
        if (!removed)
        {
            throw new NotFoundException("The material has no such conversion.");
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
