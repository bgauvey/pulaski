using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Names the lots a made lot was made from (#81), out of those its job used; an empty list goes back to pairing by time. Kept as
/// <c>made_from</c> links beside the job's own, which stay: traces then keep only the picked lots.
/// </summary>
public sealed record SetMadeFromCommand(Guid LotId, IReadOnlyList<Guid> ParentLotIds, string? Comment) : ICommand<VersionResponse>;

/// <summary>How sure a job's link between two lots is (#81); <see cref="Kept"/> is false when they don't pair, only met in the job.</summary>
internal sealed record JobLinkPrecision(bool Kept, string? Precision, string Note);

/// <summary>
/// The pairing of the job links into a set of lots (#81): a made lot with picked <c>made_from</c> lots keeps only those (declared);
/// otherwise <see cref="LotPairing"/> pairs them by time. Links that aren't a job's (split, merge) aren't judged.
/// </summary>
internal sealed class JobLinkPairs
{
    public const string PickedNote = "Picked as what it was made from.";
    public const string NotPickedNote = "Not among the lots picked as what it was made from; they only met in the job.";
    public const string NotPairedNote = "Not paired by time with this lot; they only met in the job.";

    private readonly ILookup<(Guid Parent, Guid Child), Guid> _jobs;
    private readonly Dictionary<(Guid Job, Guid Child), HashSet<Guid>> _madeFrom;
    private readonly Dictionary<(Guid Job, Guid Parent, Guid Child), LotPair> _pairs;

    private JobLinkPairs(IReadOnlyList<GenealogyLink> links, IReadOnlyList<LotPair> pairs)
    {
        var jobLinks = links.Where(l => l.JobId is not null && l.Basis is GenealogyEdge.Declared or GenealogyEdge.MadeFrom).ToList();
        _jobs = jobLinks.ToLookup(l => (l.ParentLotId, l.ChildLotId), l => l.JobId!.Value);
        _madeFrom = jobLinks.Where(l => l.Basis == GenealogyEdge.MadeFrom).GroupBy(l => (l.JobId!.Value, l.ChildLotId))
            .ToDictionary(g => g.Key, g => g.Select(l => l.ParentLotId).ToHashSet());
        _pairs = pairs.ToDictionary(p => (p.JobId, p.ParentLotId, p.ChildLotId));
    }

    public static async Task<JobLinkPairs> LoadAsync(IInventoryReadStore store, IReadOnlyCollection<Guid> childLotIds, CancellationToken cancellationToken)
    {
        var links = childLotIds.Count == 0 ? [] : await store.ListLinksToAsync(childLotIds, cancellationToken);
        var jobs = links.Where(l => l.JobId is not null && l.Basis == GenealogyEdge.Declared).Select(l => l.JobId!.Value).Distinct().ToList();
        var declarations = jobs.Count == 0 ? [] : await store.ListJobDeclarationsAsync(jobs, cancellationToken);
        return new JobLinkPairs(links, LotPairing.Pair(declarations));
    }

    /// <summary>The link from <paramref name="parent"/> to <paramref name="child"/> through a job; null when they aren't linked by one.</summary>
    public JobLinkPrecision? For(Guid parent, Guid child)
    {
        JobLinkPrecision? best = null;
        foreach (var job in _jobs[(parent, child)].Distinct())
        {
            var link = _madeFrom.TryGetValue((job, child), out var picked)
                ? picked.Contains(parent) ? new JobLinkPrecision(true, GenealogyPrecision.Declared, PickedNote) : new JobLinkPrecision(false, null, NotPickedNote)
                : _pairs.TryGetValue((job, parent, child), out var pair) ? new JobLinkPrecision(true, pair.Precision, pair.Reason) : new JobLinkPrecision(false, null, NotPairedNote);
            if (best is null || (link.Kept && (!best.Kept || GenealogyPrecision.Best(best.Precision!, link.Precision!) == link.Precision)))
            {
                best = link;
            }
        }

        return best;
    }

    /// <summary>A lot's direct links with how sure each is: a job link replaced by a pick isn't listed twice, and one that doesn't pair says so.</summary>
    public IReadOnlyList<LotLinkDto> Annotate(IEnumerable<LotLinkDto> links, Func<LotLinkDto, (Guid Parent, Guid Child)> ends)
    {
        var list = links.ToList();
        var picked = list.Where(l => l.LinkBasis == GenealogyEdge.MadeFrom).Select(l => (l.JobId, l.LotId)).ToHashSet();
        return [.. list.Where(l => !(l.LinkBasis == GenealogyEdge.Declared && picked.Contains((l.JobId, l.LotId))))
            .Select(l => ends(l) is var (parent, child) && For(parent, child) is { } link
                ? l with { Precision = link.Precision, PrecisionNote = link.Note, Unpaired = !link.Kept }
                : l)];
    }
}

/// <summary>Validates and records a made lot's picked parents: they must be lots its job used and hasn't had reversed (#81).</summary>
internal static class MadeFrom
{
    /// <summary>Runs inside the caller's transaction, with the job locked first, then the lot.</summary>
    public static async Task<IReadOnlyList<Guid>> SetAsync(
        IInventoryRepository inventory, IIdGenerator ids, Guid jobId, string jobNumber, Lot lot, IReadOnlyList<Guid> parentLotIds, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var picked = parentLotIds.Distinct().ToList();
        if (picked.Count > 0)
        {
            var used = (await inventory.ListLotsConsumedByJobAsync(jobId, cancellationToken)).ToHashSet();
            if (picked.Any(id => !used.Contains(id) || id == lot.Id))
            {
                throw new DomainRuleException($"Pick only lots {jobNumber} used; one of those picked wasn't used by it.");
            }
        }

        await inventory.SupersedeGenealogyAsync(null, lot.Id, GenealogyEdge.MadeFrom, jobId, at, cancellationToken);
        await inventory.InsertGenealogyAsync([.. picked.Select(parent => new GenealogyEdge(ids.NewId(), parent, lot.Id, jobId, GenealogyEdge.MadeFrom))], at, cancellationToken);
        return picked;
    }
}

internal sealed class SetMadeFromHandler(
    IUnitOfWork unitOfWork, IInventoryRepository inventory, IInventoryReadStore store, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<SetMadeFromCommand, VersionResponse>
{
    public const int MaxParents = 200;

    public async Task<VersionResponse> HandleAsync(SetMadeFromCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.ParentLotIds.Count <= MaxParents, nameof(command.ParentLotIds), $"At most {MaxParents} lots.")
            .Require(command.ParentLotIds.All(id => id != Guid.Empty), nameof(command.ParentLotIds), "Pick lots.")
            .ThrowIfInvalid();

        // The job that made a lot never changes, so it is read before the locks: the job first, then the lot (D-027), as declarations take them.
        var found = await store.GetLotAsync(command.LotId, cancellationToken) ?? throw new NotFoundException($"Lot {command.LotId} not found.");
        if (found.Lot.ProducedByJobId is not { } jobId)
        {
            throw new DomainRuleException("That lot wasn't made by a job, so it has no lots it was made from to pick.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var job = await inventory.GetJobForUpdateAsync(jobId, cancellationToken) ?? throw new NotFoundException($"Job {jobId} not found.");
        var current = await inventory.GetLotForUpdateAsync(command.LotId, cancellationToken) ?? throw new NotFoundException($"Lot {command.LotId} not found.");
        var lot = current.Value;
        var envelope = events.Create(EventTypes.LotMadeFromSet, "lot", lot.Id, new { lot.LotNumber, JobId = job.Id, job.JobNumber, ParentLotIds = command.ParentLotIds, Comment = comment });
        await MadeFrom.SetAsync(inventory, ids, job.Id, job.JobNumber, lot, command.ParentLotIds, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(current.Version);
    }
}
