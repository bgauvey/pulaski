using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Undoes a lot's movement with everything after it that depends on it (#79, D-077): every movement of the lot that still stands and counts
/// after it, and every movement of a lot split off it since (and of lots split off those), latest first, each a compensating movement as one
/// reversal would write. With <paramref name="DryRun"/> it does all of it and rolls back, to show what it would reverse; confirming sends
/// <paramref name="ExpectedSteps"/>, the preview's steps, and is refused if the chain has changed meanwhile.
/// </summary>
public sealed record ReverseChainCommand(
    Guid LotId, Guid EventId, Guid ReasonCodeId, string? Comment, bool DryRun, IReadOnlyList<Guid>? ExpectedSteps = null) : ICommand<ChainReversalDto>;

internal sealed class ReverseChainHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ReverseChainCommand, ChainReversalDto>
{
    private const int MaxSteps = 200;

    public async Task<ChainReversalDto> HandleAsync(ReverseChainCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.EventId != Guid.Empty, nameof(command.EventId), "Required.")
            .Require(command.ReasonCodeId != Guid.Empty, nameof(command.ReasonCodeId), "Choose a reason.")
            .Require(comment is not null, nameof(command.Comment), "Say why it is reversed.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var original = await inventory.GetEventAsync(command.LotId, command.EventId, cancellationToken)
            ?? throw new NotFoundException($"That movement of lot {command.LotId} wasn't found.");
        if (original.ReversedBy is not null || original.ReversesEventId is not null)
        {
            throw new DomainRuleException(original.ReversedBy is not null ? "That movement was reversed already." : "That movement is a reversal already.");
        }

        // Latest first: a lot split off is undone before the split that made it, and a later movement before an earlier one.
        var chain = await CollectAsync(original, cancellationToken);
        var steps = chain.OrderByDescending(e => e.EffectiveAt).ThenByDescending(e => e.OccurredAt).ToList();
        if (steps.Count > MaxSteps)
        {
            throw new DomainRuleException($"Undoing that takes back {steps.Count} movements; at most {MaxSteps} go in one step. Undo some of the later ones first.");
        }

        if (command.ExpectedSteps is { } expected && !expected.SequenceEqual(steps.Select(s => s.Id)))
        {
            throw new ConflictException("What that undo takes back has changed since it was checked. Check it again.");
        }

        // Jobs first, then lots, each in id order in SQL (D-027), so the chain can't deadlock with declarations on the same jobs and lots.
        await inventory.LockJobsAsync([.. steps.Where(s => s.JobId is not null).Select(s => s.JobId!.Value).Distinct()], cancellationToken);
        var lots = (await inventory.LockLotsAsync([.. steps.Select(s => s.LotId).Distinct()], cancellationToken)).ToDictionary(l => l.Lot.Id, l => l.Lot);
        var names = lots.ToDictionary(l => l.Key, l => l.Value.Name);
        var reverser = new ReverseMovementHandler(unitOfWork, inventory, eventLog, events, ids, timeProvider);
        string? problem = null;
        var done = 0;
        foreach (var step in steps)
        {
            // A step's other half may already be undone with an earlier step (a split part merged back, say).
            if ((await inventory.GetEventAsync(step.LotId, step.Id, cancellationToken))?.ReversedBy is not null)
            {
                done++;
                continue;
            }

            try
            {
                await reverser.ReverseAsync(new ReverseRequest(step.LotId, null, step.Id, command.ReasonCodeId), comment!, cancellationToken);
                done++;
            }
            catch (Exception ex) when (ex is DomainRuleException or ConflictException or NotFoundException)
            {
                problem = $"{Describe(step, names)}: {ex.Message}";
                break;
            }
        }

        var applied = problem is null && !command.DryRun;
        if (applied)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ChainReversalDto
        {
            Applied = applied,
            Steps = [.. steps.Select(s => new ChainStepDto(s.LotId, names.GetValueOrDefault(s.LotId, ""), s.Id, s.Movement.Kind, s.Movement.QtyDelta, s.EffectiveAt, s.JobNumber))],
            Reversible = problem is null,
            Problem = problem,
            DoneBeforeProblem = problem is null ? steps.Count : done,
        };
    }

    // The movement, what stands after it on its lot, and everything on lots split off it since, recursively; a pair's other half on a lot
    // outside the chain is undone with its step.
    private async Task<List<LotEventDetail>> CollectAsync(LotEventDetail original, CancellationToken cancellationToken)
    {
        var chain = new List<LotEventDetail> { original };
        var queue = new Queue<(Guid Lot, LotEventDetail After)>();
        queue.Enqueue((original.LotId, original));
        var visited = new HashSet<Guid> { original.LotId };
        while (queue.Count > 0 && chain.Count <= MaxSteps)
        {
            var (lotId, after) = queue.Dequeue();
            foreach (var e in await inventory.ListStandingAfterAsync(lotId, after, cancellationToken))
            {
                chain.Add(e);
                if (e.Movement.Kind == LotEventKind.Split && e.Movement.QtyDelta < 0 && e.RelatedLotId is { } part && visited.Add(part)
                    && await inventory.GetCounterpartAsync(e, cancellationToken) is { } made)
                {
                    queue.Enqueue((part, made));
                }
            }
        }

        return chain;
    }

    private static string Describe(LotEventDetail step, IReadOnlyDictionary<Guid, string> names) =>
        $"{names.GetValueOrDefault(step.LotId, "A lot")}'s {step.Movement.Kind.Replace('_', ' ')}{(step.JobNumber is { } job ? $" into {job}" : "")}";
}
