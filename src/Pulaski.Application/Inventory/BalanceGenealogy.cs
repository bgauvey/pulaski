using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Where a balance's withdrawals came from (D-060), by its location's flow model.</summary>
public sealed record GetBalanceAllocationQuery(Guid LotId) : IQuery<BalanceAllocationDto>;

/// <summary>
/// Sets how a balance at a location flows (D-060): mixed or first in, first out, and the least time material stays; and how its level
/// readings are read (#81): at or below <c>EmptyLevel</c> it is empty, and within <c>LevelTolerance</c> the level agrees with declared stock.
/// </summary>
public sealed record SetFlowCommand(Guid EquipmentId, long ExpectedVersion, string FlowModel, int? MinResidenceMinutes, decimal? EmptyLevel = null, decimal? LevelTolerance = null)
    : ICommand<VersionResponse>;

internal static class BalanceGenealogy
{
    public const int MaxResidenceMinutes = 60 * 24 * 90;

    public static IReadOnlyList<WithdrawalAllocation> Allocate(BalanceHistory history, BalanceLevels? levels) =>
        BalanceAllocation.Allocate(
            history.Movements.Select(m => new BalanceMovement(m.EventId, m.At, m.Delta, m.SourceLotId, m.JobId, m.Kind)),
            history.FlowModel,
            history.MinResidence,
            levels);

    /// <summary>
    /// The level evidence in the material's base unit (#81): empty points always; readings and the tolerance only when the level tag's unit
    /// converts to the base unit, else the level isn't compared with declared stock. Null when the location has no level tag.
    /// </summary>
    public static async Task<BalanceLevels?> LevelsAsync(BalanceHistory history, IUnitReadStore units, CancellationToken cancellationToken)
    {
        if (history.Levels is not { } data)
        {
            return null;
        }

        decimal? factor = null;
        if (data.LevelUomId is { } levelUom && data.Tolerance is not null)
        {
            var all = await units.GetUnitsAsync(cancellationToken);
            var conversions = await units.GetConversionsAsync([data.MaterialId], cancellationToken);
            factor = UnitConversions.For(data.MaterialId, all, conversions).Factor(levelUom, data.BaseUomId);
        }

        return factor is { } f
            ? new BalanceLevels(data.Empties, data.ReadingBefore.ToDictionary(r => r.Key, r => r.Value * f), data.Tolerance * f)
            : new BalanceLevels(data.Empties, new Dictionary<Guid, decimal>(), null);
    }

    /// <summary>What the estimate rests on, for screens to say beside it.</summary>
    public static string Basis(BalanceHistory history, BalanceLevels? levels) =>
        (history.FlowModel == FlowModels.Fifo ? "First in, first out" : "Mixed: every arrival still there gives its share")
        + (history.MinResidence is { } residence ? $", at least {(int)residence.TotalMinutes} min in the balance" : "")
        + (levels switch
        {
            { Tolerance: not null } => ". Worked out from the declared movements, checked against the level readings.",
            { Empties.Count: > 0 } => ". Worked out from the declared movements; the level readings mark when it was empty.",
            _ => ". Worked out from the declared movements, not measured.",
        });
}

internal sealed class GetBalanceAllocationHandler(IInventoryReadStore store, IUnitReadStore units) : IQueryHandler<GetBalanceAllocationQuery, BalanceAllocationDto>
{
    public async Task<BalanceAllocationDto> HandleAsync(GetBalanceAllocationQuery query, CancellationToken cancellationToken)
    {
        var history = await store.GetBalanceHistoryAsync(query.LotId, cancellationToken)
            ?? throw new NotFoundException($"Lot {query.LotId} isn't a balance.");
        var levels = await BalanceGenealogy.LevelsAsync(history, units, cancellationToken);
        var rows = history.Movements.ToDictionary(m => m.EventId);
        var lotNumbers = history.Movements.Where(m => m.SourceLotId is not null).GroupBy(m => m.SourceLotId!.Value).ToDictionary(g => g.Key, g => g.First().SourceLotNumber);
        return new BalanceAllocationDto
        {
            LotId = query.LotId,
            FlowModel = history.FlowModel,
            MinResidenceMinutes = history.MinResidence is { } r ? (int)r.TotalMinutes : null,
            Basis = BalanceGenealogy.Basis(history, levels),
            Withdrawals = [.. BalanceGenealogy.Allocate(history, levels).Reverse().Select(a => new WithdrawalAllocationDto
            {
                EventId = a.Withdrawal.EventId,
                At = a.Withdrawal.At,
                Kind = a.Withdrawal.Kind,
                Quantity = -a.Withdrawal.Delta,
                JobId = a.Withdrawal.JobId,
                JobNumber = rows[a.Withdrawal.EventId].JobNumber,
                Precision = a.Precision,
                Reason = a.Reason,
                Sources = [.. a.Sources.Select(s => new AllocationSourceDto
                {
                    LotId = s.SourceLotId,
                    LotNumber = s.SourceLotId is { } id ? lotNumbers.GetValueOrDefault(id) : null,
                    Quantity = s.Quantity,
                    Precision = s.Precision,
                })],
            })],
        };
    }
}

internal sealed class SetFlowHandler(IUnitOfWork unitOfWork, IInventoryRepository inventory, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetFlowCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetFlowCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(FlowModels.All.Contains(command.FlowModel), "FlowModel", "Mixed or first in, first out (fifo).")
            .Require(command.MinResidenceMinutes is null or (>= 0 and <= BalanceGenealogy.MaxResidenceMinutes), "MinResidenceMinutes", "0 to 129600 minutes (90 days), or blank.")
            .Require(command.LevelTolerance is null or >= 0, "LevelTolerance", "Zero or more, or blank.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await inventory.GetFlowSettingsForUpdateAsync(command.EquipmentId, cancellationToken)
            ?? throw new NotFoundException($"Equipment {command.EquipmentId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        if (!current.Value.HoldsInventory)
        {
            throw new DomainRuleException($"{current.Value.Code} doesn't hold inventory.");
        }

        var updated = current.Value with
        {
            FlowModel = command.FlowModel,
            MinResidence = command.MinResidenceMinutes is { } minutes and > 0 ? TimeSpan.FromMinutes(minutes) : null,
            EmptyLevel = command.EmptyLevel,
            LevelTolerance = command.LevelTolerance,
        };
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.EquipmentFlowSet, "equipment", updated.EquipmentId, new { Before = current.Value, After = updated });
        var version = await inventory.UpdateFlowSettingsAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

/// <summary>
/// Narrows a trace (#81, D-060). A job's link keeps only what its lots pair with (<see cref="JobLinkPairs"/>): the lots a made lot was
/// picked as made from, else those paired by time. Through a balance whose lots mix, a trace back from what a job made keeps only the
/// arrivals the flow model gives that job's withdrawals, and a trace forward from an arrival keeps only what was made from withdrawals it
/// fed. Each kept link says how sure it is (the least sure of what applies); lots on a split or merge are left as they are.
/// </summary>
internal static class TraceNarrowing
{
    public static async Task<IReadOnlyList<LotTraceNodeDto>> ApplyAsync(
        IReadOnlyList<LotTraceNodeDto> nodes, bool forward, Guid startLotId, IInventoryReadStore store, IUnitReadStore units, CancellationToken cancellationToken)
    {
        var byId = nodes.ToDictionary(n => n.LotId);
        var removed = new HashSet<Guid>();
        var annotated = new Dictionary<Guid, (string? Precision, string Note)>();

        void Mark(Guid lotId, string? precision, string note) =>
            annotated[lotId] = annotated.TryGetValue(lotId, out var had)
                ? (had.Precision is null ? precision : precision is null ? had.Precision : GenealogyPrecision.Worst(had.Precision, precision), had.Note + " " + note)
                : (precision, note);

        // Job links: only the lots that pair.
        var linked = nodes.Where(n => n.ViaLotId is not null).Select(n => (Node: n, Parent: forward ? n.ViaLotId!.Value : n.LotId, Child: forward ? n.LotId : n.ViaLotId!.Value)).ToList();
        var pairs = await JobLinkPairs.LoadAsync(store, [.. linked.Select(l => l.Child).Distinct()], cancellationToken);
        foreach (var (node, parent, child) in linked)
        {
            switch (pairs.For(parent, child))
            {
                case { Kept: false }:
                    removed.Add(node.LotId);
                    break;
                case { } link:
                    Mark(node.LotId, link.Precision, link.Note);
                    break;
            }
        }

        foreach (var balance in nodes.Where(n => n.IsBulk && n.LotId != startLotId).OrderBy(n => n.Depth).ToList())
        {
            if (removed.Contains(balance.LotId) || balance.ViaLotId is not { } viaId || !byId.TryGetValue(viaId, out var via)
                || await store.GetBalanceHistoryAsync(balance.LotId, cancellationToken) is not { } history)
            {
                continue;
            }

            var levels = await BalanceGenealogy.LevelsAsync(history, units, cancellationToken);
            var allocations = BalanceGenealogy.Allocate(history, levels);
            var basis = BalanceGenealogy.Basis(history, levels);
            var beyond = nodes.Where(n => n.ViaLotId == balance.LotId && !removed.Contains(n.LotId)).ToList();
            if (!forward)
            {
                // Back from what a job made: only the arrivals the flow model gives that job's withdrawals.
                if (via.ProducedByJobId is not { } jobId)
                {
                    foreach (var node in beyond)
                    {
                        Mark(node.LotId, GenealogyPrecision.Possible, "Not made by a job from this balance, so every arrival is kept. " + basis);
                    }

                    continue;
                }

                var fed = allocations.Where(a => a.Withdrawal.JobId == jobId).ToList();
                var sources = fed.SelectMany(a => a.Sources).Where(s => s.SourceLotId is not null)
                    .GroupBy(s => s.SourceLotId!.Value).ToDictionary(g => g.Key, g => GenealogyPrecision.WorstOf(g.Select(s => s.Precision)));
                Mark(balance.LotId, GenealogyPrecision.WorstOf(fed.Select(a => a.Precision)), basis);
                foreach (var node in beyond)
                {
                    if (sources.TryGetValue(node.LotId, out var precision))
                    {
                        Mark(node.LotId, precision, basis);
                    }
                    else
                    {
                        removed.Add(node.LotId);
                    }
                }
            }
            else
            {
                // Forward from an arrival: only what was made from withdrawals it fed.
                var jobs = allocations.Where(a => a.Sources.Any(s => s.SourceLotId == viaId)).GroupBy(a => a.Withdrawal.JobId)
                    .Where(g => g.Key is not null)
                    .ToDictionary(g => g.Key!.Value, g => GenealogyPrecision.WorstOf(g.SelectMany(a => a.Sources.Where(s => s.SourceLotId == viaId)).Select(s => s.Precision)));
                foreach (var node in beyond)
                {
                    if (node.ProducedByJobId is { } jobId && jobs.TryGetValue(jobId, out var precision))
                    {
                        Mark(node.LotId, precision, basis);
                    }
                    else if (node.ProducedByJobId is null)
                    {
                        Mark(node.LotId, GenealogyPrecision.Possible, "Not made by a job from this balance. " + basis);
                    }
                    else
                    {
                        removed.Add(node.LotId);
                    }
                }
            }
        }

        // What was reached only through a lot left out goes too.
        int before;
        do
        {
            before = removed.Count;
            foreach (var node in nodes.Where(n => n.ViaLotId is { } via && removed.Contains(via)))
            {
                removed.Add(node.LotId);
            }
        }
        while (removed.Count != before);

        return [.. nodes.Where(n => !removed.Contains(n.LotId)).Select(n => annotated.TryGetValue(n.LotId, out var a)
            ? n with { Precision = a.Precision, PrecisionNote = a.Note }
            : n)];
    }
}
