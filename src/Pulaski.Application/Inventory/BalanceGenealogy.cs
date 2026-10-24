using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Where a balance's withdrawals came from (D-060), by its location's flow model.</summary>
public sealed record GetBalanceAllocationQuery(Guid LotId) : IQuery<BalanceAllocationDto>;

/// <summary>Sets how a balance at a location flows (D-060): mixed or first in, first out, and the least time material stays.</summary>
public sealed record SetFlowCommand(Guid EquipmentId, long ExpectedVersion, string FlowModel, int? MinResidenceMinutes) : ICommand<VersionResponse>;

internal static class BalanceGenealogy
{
    public const int MaxResidenceMinutes = 60 * 24 * 90;

    public static IReadOnlyList<WithdrawalAllocation> Allocate(BalanceHistory history) =>
        BalanceAllocation.Allocate(
            history.Movements.Select(m => new BalanceMovement(m.EventId, m.At, m.Delta, m.SourceLotId, m.JobId, m.Kind)),
            history.FlowModel,
            history.MinResidence);

    /// <summary>What the estimate rests on, for screens to say beside it.</summary>
    public static string Basis(BalanceHistory history) =>
        (history.FlowModel == FlowModels.Fifo ? "First in, first out" : "Mixed: every arrival still there gives its share")
        + (history.MinResidence is { } residence ? $", at least {(int)residence.TotalMinutes} min in the balance" : "")
        + ". Worked out from the declared movements, not measured.";
}

internal sealed class GetBalanceAllocationHandler(IInventoryReadStore store) : IQueryHandler<GetBalanceAllocationQuery, BalanceAllocationDto>
{
    public async Task<BalanceAllocationDto> HandleAsync(GetBalanceAllocationQuery query, CancellationToken cancellationToken)
    {
        var history = await store.GetBalanceHistoryAsync(query.LotId, cancellationToken)
            ?? throw new NotFoundException($"Lot {query.LotId} isn't a balance.");
        var rows = history.Movements.ToDictionary(m => m.EventId);
        var lotNumbers = history.Movements.Where(m => m.SourceLotId is not null).GroupBy(m => m.SourceLotId!.Value).ToDictionary(g => g.Key, g => g.First().SourceLotNumber);
        return new BalanceAllocationDto
        {
            LotId = query.LotId,
            FlowModel = history.FlowModel,
            MinResidenceMinutes = history.MinResidence is { } r ? (int)r.TotalMinutes : null,
            Basis = BalanceGenealogy.Basis(history),
            Withdrawals = [.. BalanceGenealogy.Allocate(history).Reverse().Select(a => new WithdrawalAllocationDto
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
/// Narrows a trace through balances whose lots mix (D-060). Through a balance, a trace back from what a job made keeps only the
/// arrivals the flow model gives that job's withdrawals, and a trace forward from an arrival keeps only what was made from
/// withdrawals it fed; each kept link says how sure it is. Lots on an ordinary declared link are left as they are.
/// </summary>
internal static class TraceNarrowing
{
    public static async Task<IReadOnlyList<LotTraceNodeDto>> ApplyAsync(
        IReadOnlyList<LotTraceNodeDto> nodes, bool forward, Guid startLotId, IInventoryReadStore store, CancellationToken cancellationToken)
    {
        var byId = nodes.ToDictionary(n => n.LotId);
        var removed = new HashSet<Guid>();
        var annotated = new Dictionary<Guid, (string Precision, string Note)>();
        foreach (var balance in nodes.Where(n => n.IsBulk && n.LotId != startLotId).OrderBy(n => n.Depth).ToList())
        {
            if (removed.Contains(balance.LotId) || balance.ViaLotId is not { } viaId || !byId.TryGetValue(viaId, out var via)
                || await store.GetBalanceHistoryAsync(balance.LotId, cancellationToken) is not { } history)
            {
                continue;
            }

            var allocations = BalanceGenealogy.Allocate(history);
            var basis = BalanceGenealogy.Basis(history);
            var beyond = nodes.Where(n => n.ViaLotId == balance.LotId && !removed.Contains(n.LotId)).ToList();
            if (!forward)
            {
                // Back from what a job made: only the arrivals the flow model gives that job's withdrawals.
                if (via.ProducedByJobId is not { } jobId)
                {
                    Mark(beyond, GenealogyPrecision.Possible, "Not made by a job from this balance, so every arrival is kept. " + basis);
                    continue;
                }

                var fed = allocations.Where(a => a.Withdrawal.JobId == jobId).ToList();
                var sources = fed.SelectMany(a => a.Sources).Where(s => s.SourceLotId is not null)
                    .GroupBy(s => s.SourceLotId!.Value).ToDictionary(g => g.Key, g => g.Aggregate(GenealogyPrecision.Exact, (w, s) => GenealogyPrecision.Worst(w, s.Precision)));
                annotated[balance.LotId] = (fed.Aggregate(GenealogyPrecision.Exact, (w, a) => GenealogyPrecision.Worst(w, a.Precision)), basis);
                foreach (var node in beyond)
                {
                    if (sources.TryGetValue(node.LotId, out var precision))
                    {
                        annotated[node.LotId] = (precision, basis);
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
                    .ToDictionary(g => g.Key!.Value, g => g.SelectMany(a => a.Sources.Where(s => s.SourceLotId == viaId))
                        .Aggregate(GenealogyPrecision.Exact, (w, s) => GenealogyPrecision.Worst(w, s.Precision)));
                foreach (var node in beyond)
                {
                    if (node.ProducedByJobId is { } jobId && jobs.TryGetValue(jobId, out var precision))
                    {
                        annotated[node.LotId] = (precision, basis);
                    }
                    else if (node.ProducedByJobId is null)
                    {
                        annotated[node.LotId] = (GenealogyPrecision.Possible, "Not made by a job from this balance. " + basis);
                    }
                    else
                    {
                        removed.Add(node.LotId);
                    }
                }
            }

            void Mark(IEnumerable<LotTraceNodeDto> marked, string precision, string note)
            {
                foreach (var node in marked)
                {
                    annotated[node.LotId] = (precision, note);
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
