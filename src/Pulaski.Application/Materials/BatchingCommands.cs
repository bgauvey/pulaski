using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Materials;

/// <summary>
/// Makes a material in batches, changes its batch settings, or makes it continuous again (null size, D-036).
/// The rule defaults from the material's type the first time: exact for finished goods, near otherwise.
/// </summary>
public sealed record SetBatchingCommand(Guid MaterialId, long ExpectedVersion, decimal? BatchSize, decimal TolerancePct, string? Rule)
    : ICommand<UpdateMaterialResponse>;

internal sealed class SetBatchingHandler(
    IUnitOfWork unitOfWork,
    IMaterialAdminRepository materials,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetBatchingCommand, UpdateMaterialResponse>
{
    public async Task<UpdateMaterialResponse> HandleAsync(SetBatchingCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.BatchSize is null or > 0, nameof(command.BatchSize), "Greater than zero, or blank for a material made continuously.")
            .Require(command.TolerancePct is >= 0 and <= 50, nameof(command.TolerancePct), "Between 0 and 50 percent.")
            .Require(command.Rule is null || BatchRule.All.Contains(command.Rule), nameof(command.Rule), "exact or near.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await materials.GetForUpdateAsync(command.MaterialId, cancellationToken)
            ?? throw new NotFoundException($"Material {command.MaterialId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var material = current.Value;
        var before = await materials.GetBatchingAsync(material.Id, cancellationToken);
        var after = command.BatchSize is { } size
            ? Batching.Create(size, command.TolerancePct, command.Rule ?? before.Rule ?? BatchRule.DefaultFor(material.MaterialType))
            : null;
        if (before.Size == after?.Size && before.Rule == after?.Rule && (after is null || before.TolerancePct == after.TolerancePct))
        {
            return new UpdateMaterialResponse(current.Version, []);
        }

        var envelope = events.Create(
            EventTypes.MaterialBatchingSet, "material", material.Id,
            new { Before = new { BatchSize = before.Size, before.TolerancePct, BatchRule = before.Rule }, After = new { BatchSize = after?.Size, TolerancePct = after?.TolerancePct ?? command.TolerancePct, BatchRule = after?.Rule } });
        var version = await materials.UpdateBatchingAsync(material.Id, after?.Size, after?.TolerancePct ?? command.TolerancePct, after?.Rule, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        // Orders already made keep their quantity; say which are no longer whole batches.
        var notes = new List<string>();
        if (after is not null)
        {
            var converter = UnitConversions.For(material.Id, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([material.Id], cancellationToken));
            var partial = (await materials.ListOpenOrdersAsync(material.Id, cancellationToken))
                .Where(o => !converter.CanConvert(o.UomId, material.BaseUomId) || after.WholeBatches(converter.Convert(o.Quantity, o.UomId, material.BaseUomId)) is null)
                .Select(o => o.OrderNumber)
                .ToList();
            if (partial.Count > 0)
            {
                notes.Add($"Open work orders that aren't whole batches of {Batching.Qty(after.Size)}: {string.Join(", ", partial)}. They keep their quantity; new orders and jobs are checked.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new UpdateMaterialResponse(version, notes);
    }
}
