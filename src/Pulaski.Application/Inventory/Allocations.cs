using Microsoft.Extensions.Logging;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Inventory;

/// <summary>A job as allocating to it needs it (D-064).</summary>
public sealed record AllocationJob(Guid Id, string JobNumber, Guid SiteId, string Status, DateTimeOffset? PlannedStart);

/// <summary>One planned row of a job's component, with everything consumed of it (the same on each row), in the base unit.</summary>
public sealed record AllocationNeedRow(
    Guid MaterialId, string MaterialCode, string MaterialName, Guid BaseUomId, string BaseUomCode, decimal PlannedQty, Guid UomId, decimal ConsumedQty);

/// <summary>An active allocation with the job it is for.</summary>
public sealed record HeldAllocation(LotAllocation Allocation, string JobNumber)
{
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A lot of one of the job's components at its site, as a candidate, with where it is.</summary>
public sealed record AllocationLot(Guid MaterialId, AllocationCandidate Candidate, string? LocationCode);

/// <summary>An approved alternate for one of a job's components (#82), from its BOM line: <see cref="Ratio"/> of its base units per one of the component's.</summary>
public sealed record AllocationAlternate(Guid ComponentId, Guid MaterialId, string MaterialCode, Guid BaseUomId, string BaseUomCode, decimal Ratio, int Priority);

/// <summary>
/// Everything allocating to a job reads (D-064): the job, its components, its allocations and the lots that could feed them; the components'
/// approved alternates and the site's lot preferences (#82).
/// </summary>
public sealed record JobAllocationSource(
    AllocationJob Job,
    IReadOnlyList<AllocationNeedRow> Needs,
    IReadOnlyList<HeldAllocation> Allocations,
    IReadOnlyList<AllocationLot> Lots,
    IReadOnlyList<AllocationAlternate>? Alternates = null,
    IReadOnlyList<string>? Preferences = null);

/// <summary>
/// An active allocation for the shortage calculator, at most what its lot holds and only of available stock: what it covers of the job's
/// component (<see cref="MaterialId"/>, in its base unit), and what it takes out of the lot's own material (<see cref="LotMaterialId"/>).
/// They differ only for an alternate (#82).
/// </summary>
public sealed record AllocatedStock(Guid JobId, Guid MaterialId, decimal Quantity, Guid LotMaterialId, decimal LotQuantity);

/// <summary>How a site allocates (#82).</summary>
public sealed record AllocationSettings(Guid SiteId, bool KeepCurrent, bool HardReservation, IReadOnlyList<string> Preferences, DateTimeOffset? RefreshedAt = null)
{
    public static AllocationSettings Default(Guid siteId) => new(siteId, false, false, []);
}

/// <summary>Write side of allocations (D-064); runs inside the current <see cref="IUnitOfWork"/>, after the job and lots are locked.</summary>
public interface IAllocationRepository
{
    /// <summary>The job, its components, allocations and candidate lots, as this transaction sees them.</summary>
    Task<JobAllocationSource?> LoadAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Locks the active allocations of the lots, in id order.</summary>
    Task<IReadOnlyList<HeldAllocation>> LockActiveOfLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>An allocation, without locking; null when there is none.</summary>
    Task<LotAllocation?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The site's production jobs that can still be allocated to and plan components, in id order.</summary>
    Task<IReadOnlyList<Guid>> ListOpenJobsAsync(Guid siteId, CancellationToken cancellationToken);

    Task InsertAsync(LotAllocation allocation, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The site's allocation settings as this transaction sees them; the defaults when it has none (#82).</summary>
    Task<AllocationSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's settings, locked, made with the defaults first when it has none; with their version, 0 when just made.</summary>
    Task<Versioned<AllocationSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken);

    Task<long> UpdateSettingsAsync(AllocationSettings settings, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Sites keeping allocations current whose stock or schedule changed since their last refresh: the latest such change is older than
    /// <paramref name="quiet"/>, or the last refresh older than <paramref name="atMost"/>, so a burst of movements refreshes once.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListSitesDueForRefreshAsync(TimeSpan quiet, TimeSpan atMost, CancellationToken cancellationToken);

    Task MarkRefreshedAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes quantity, status and note; an allocation that is no longer active gets its end time and who ended it.</summary>
    Task UpdateAsync(LotAllocation allocation, string actor, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side of allocations (D-064).</summary>
public interface IAllocationReadStore
{
    Task<JobAllocationSource?> LoadAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The active allocations of a lot, with their jobs.</summary>
    Task<IReadOnlyList<LotAllocationDto>> ListOfLotAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>Active allocations for the components at the site (as themselves or by an alternate), for shortages.</summary>
    Task<IReadOnlyList<AllocatedStock>> ListAllocatedAsync(Guid siteId, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken);

    /// <summary>The site's allocation settings and their version (0 while it has none saved).</summary>
    Task<(AllocationSettings Settings, long Version)> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>Active allocations of open jobs at the site, of one job or the jobs of equipment and what is below it, for picking, ordered by location (#82).</summary>
    Task<PickingListDto> GetPickingListAsync(Guid siteId, Guid? jobId, Guid? lineId, CancellationToken cancellationToken);
}

/// <summary>One component worked out: need, use, allocations with their problems, and what to suggest.</summary>
public sealed record ComponentPlan(
    AllocationNeedRow Material,
    decimal? Need,
    decimal Consumed,
    decimal Allocated,
    decimal Remaining,
    IReadOnlyList<(HeldAllocation Allocation, AllocationLot? Lot, string? Problem)> Allocations,
    IReadOnlyList<(LotPick Pick, AllocationLot Lot, AllocationAlternate? Alternate)> Picks,
    IReadOnlyList<(LotExclusion Exclusion, AllocationLot Lot)> Excluded,
    string? Problem)
{
    /// <summary>What the suggestions cover of the component, in its base unit: an alternate's at its ratio.</summary>
    public decimal Suggested => Picks.Sum(p => p.Alternate is { } a ? Math.Round(p.Pick.Quantity / a.Ratio, 6, MidpointRounding.AwayFromZero) : p.Pick.Quantity);
}

/// <summary>Works out a job's components against its allocations and the lots there are (D-064). Shared by the screen and by refresh.</summary>
internal static class AllocationPlanner
{
    private static readonly HashSet<string> Ended = ["completed", "closed", "cancelled"];

    public static bool CanAllocate(AllocationJob job) => !Ended.Contains(job.Status);

    /// <summary>The job's planned start, or now when that has passed or isn't set: lots must not expire before it.</summary>
    public static DateTimeOffset NeededBy(AllocationJob job, DateTimeOffset now) => job.PlannedStart is { } start && start > now ? start : now;

    public static IReadOnlyList<ComponentPlan> Plan(
        JobAllocationSource source, IReadOnlyList<Unit> units, IReadOnlyList<MaterialConversionRow> conversions, DateTimeOffset now)
    {
        var neededBy = NeededBy(source.Job, now);
        var plans = new List<ComponentPlan>();
        foreach (var rows in source.Needs.GroupBy(n => n.MaterialId))
        {
            var first = rows.First();
            var converter = UnitConversions.For(first.MaterialId, units, conversions);
            decimal? need = 0m;
            string? problem = null;
            foreach (var row in rows)
            {
                if (converter.Factor(row.UomId, row.BaseUomId) is { } factor)
                {
                    need += Math.Round(row.PlannedQty * factor, 6, MidpointRounding.AwayFromZero);
                }
                else
                {
                    need = null;
                    problem = $"{first.MaterialCode} is planned in a unit that doesn't convert to {first.BaseUomCode}; nothing is suggested.";
                    break;
                }
            }

            var lots = source.Lots.Where(l => l.MaterialId == first.MaterialId).ToList();
            var byLot = source.Lots.GroupBy(l => l.Candidate.LotId).ToDictionary(g => g.Key, g => g.First());
            var mine = source.Allocations.Where(a => a.Allocation.ComponentId == first.MaterialId).ToList();
            var allocated = mine.Sum(a => a.Allocation.Covers);
            var remaining = need is { } n ? Math.Max(0m, n - first.ConsumedQty - allocated) : 0m;
            var checkedAllocations = mine.Select(a =>
            {
                var lot = byLot.GetValueOrDefault(a.Allocation.LotId);
                var issue = lot is null
                    ? "The lot is no longer on hand."
                    : LotRecommendation.AllocationProblem(a.Allocation, lot.Candidate, neededBy, a.Allocation.Ratio is null
                        ? first.BaseUomCode
                        : source.Alternates?.FirstOrDefault(x => x.MaterialId == a.Allocation.MaterialId)?.BaseUomCode ?? first.BaseUomCode);
                return (a, lot, issue);
            }).ToList();
            var (picks, excluded) = need is null
                ? ([], [])
                : LotRecommendation.Recommend(remaining, lots.Select(l => l.Candidate), neededBy, first.BaseUomCode, source.Preferences);
            var chosen = picks.Select(p => (p, byLot[p.Lot.LotId], (AllocationAlternate?)null)).ToList();

            // What the planned material can't cover, its approved alternates can (#82), by priority, at their ratio.
            var left = remaining - picks.Sum(p => p.Quantity);
            foreach (var alternate in (source.Alternates ?? []).Where(a => a.ComponentId == first.MaterialId).OrderBy(a => a.Priority))
            {
                if (left <= 0)
                {
                    break;
                }

                var alternateLots = source.Lots.Where(l => l.MaterialId == alternate.MaterialId).ToList();
                var (alternatePicks, _) = LotRecommendation.Recommend(
                    Math.Round(left * alternate.Ratio, 6, MidpointRounding.AwayFromZero), alternateLots.Select(l => l.Candidate), neededBy, alternate.BaseUomCode, source.Preferences);
                foreach (var pick in alternatePicks)
                {
                    chosen.Add((pick with { Reason = $"Alternate for {first.MaterialCode} ({alternate.Ratio:0.######} {alternate.BaseUomCode} per {first.BaseUomCode}): {pick.Reason}" },
                        byLot[pick.Lot.LotId], alternate));
                    left -= Math.Round(pick.Quantity / alternate.Ratio, 6, MidpointRounding.AwayFromZero);
                }
            }

            plans.Add(new ComponentPlan(
                first, need, first.ConsumedQty, allocated, remaining, checkedAllocations,
                chosen,
                [.. excluded.Select(e => (e, byLot[e.Lot.LotId]))],
                problem));
        }

        return [.. plans.OrderBy(p => p.Material.MaterialCode, StringComparer.Ordinal)];
    }
}

/// <summary>A job's allocations and what to use next (D-064).</summary>
public sealed record GetJobAllocationsQuery(Guid JobId) : IQuery<JobAllocationsDto>;

/// <summary>Sets quantity of a lot aside for one of the job's components; more of the same lot tops its allocation up.</summary>
public sealed record AllocateLotCommand(Guid JobId, Guid LotId, decimal Quantity, Guid UomId, string? Note = null) : ICommand<AllocationChangeResponse>;

/// <summary>Allocates what is still needed from the recommended lots: for one component, or all of them.</summary>
public sealed record AllocateRecommendedCommand(Guid JobId, Guid? MaterialId = null) : ICommand<AllocationChangeResponse>;

/// <summary>Gives an allocation's stock back to everyone.</summary>
public sealed record ReleaseAllocationCommand(Guid JobId, Guid AllocationId, string? Note = null) : ICommand<AllocationChangeResponse>;

/// <summary>
/// Puts a job's allocations right as stock and the schedule have changed (D-064): releases what is no longer usable, trims what a
/// lot can no longer give, then allocates what is still needed from the recommendations.
/// </summary>
public sealed record RefreshAllocationsCommand(Guid JobId) : ICommand<AllocationChangeResponse>;

/// <summary>Refreshes every open job of a site that plans components.</summary>
public sealed record RefreshSiteAllocationsCommand(Guid SiteId) : ICommand<AllocationChangeResponse>;

/// <summary>How a site allocates (#82).</summary>
public sealed record GetAllocationSettingsQuery(Guid SiteId) : IQuery<AllocationSettingsDto>;

/// <summary>Sets how a site allocates (#82): keep current, hard reservation, and the preferences in order (each at most once).</summary>
public sealed record SetAllocationSettingsCommand(Guid SiteId, long ExpectedVersion, bool KeepCurrent, bool HardReservation, IReadOnlyList<string> Preferences)
    : ICommand<VersionResponse>;

/// <summary>What to fetch for one job, or for every open job on a line, or the whole site (#82): active allocations by location.</summary>
public sealed record GetPickingListQuery(Guid SiteId, Guid? JobId = null, Guid? LineId = null) : IQuery<PickingListDto>;

internal sealed class GetJobAllocationsHandler(IAllocationReadStore store, IUnitReadStore units, TimeProvider time) : IQueryHandler<GetJobAllocationsQuery, JobAllocationsDto>
{
    public async Task<JobAllocationsDto> HandleAsync(GetJobAllocationsQuery query, CancellationToken cancellationToken)
    {
        var source = await store.LoadAsync(query.JobId, cancellationToken) ?? throw new NotFoundException($"Production job {query.JobId} not found.");
        var now = time.GetUtcNow();
        var alternates = source.Alternates ?? [];
        var plans = AllocationPlanner.Plan(
            source, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([.. source.Needs.Select(n => n.MaterialId).Distinct()], cancellationToken), now);
        return new JobAllocationsDto
        {
            JobId = source.Job.Id,
            JobNumber = source.Job.JobNumber,
            SiteId = source.Job.SiteId,
            JobStatus = source.Job.Status,
            NeededBy = AllocationPlanner.NeededBy(source.Job, now),
            CanAllocate = AllocationPlanner.CanAllocate(source.Job),
            Components = [.. plans.Select(p => new ComponentAllocationDto
            {
                MaterialId = p.Material.MaterialId,
                MaterialCode = p.Material.MaterialCode,
                MaterialName = p.Material.MaterialName,
                UomId = p.Material.BaseUomId,
                UomCode = p.Material.BaseUomCode,
                NeedQty = p.Need,
                ConsumedQty = p.Consumed,
                AllocatedQty = p.Allocated,
                RemainingQty = p.Remaining,
                Problem = p.Problem,
                Allocations = [.. p.Allocations.Select(a => new LotAllocationDto
                {
                    Id = a.Allocation.Allocation.Id,
                    JobId = source.Job.Id,
                    JobNumber = source.Job.JobNumber,
                    MaterialId = p.Material.MaterialId,
                    LotId = a.Allocation.Allocation.LotId,
                    LotLabel = a.Lot?.Candidate.Label ?? "Lot no longer on hand",
                    LocationCode = a.Lot?.LocationCode,
                    Quantity = a.Allocation.Allocation.Quantity,
                    AllocatedQty = a.Allocation.Allocation.AllocatedQty,
                    UomCode = alternates.FirstOrDefault(x => a.Allocation.Allocation.Ratio is not null && x.MaterialId == a.Allocation.Allocation.MaterialId)?.BaseUomCode
                        ?? p.Material.BaseUomCode,
                    AlternateMaterialCode = a.Allocation.Allocation.Ratio is null ? null
                        : alternates.FirstOrDefault(x => x.MaterialId == a.Allocation.Allocation.MaterialId)?.MaterialCode ?? "an alternate",
                    Ratio = a.Allocation.Allocation.Ratio,
                    Basis = a.Allocation.Allocation.Basis,
                    Note = a.Allocation.Allocation.Note,
                    CreatedAt = a.Allocation.CreatedAt,
                    CreatedBy = a.Allocation.CreatedBy,
                    Problem = a.Problem,
                })],
                Recommendations = [.. p.Picks.Select(x => new LotRecommendationDto
                {
                    LotId = x.Lot.Candidate.LotId,
                    LotLabel = x.Lot.Candidate.Label,
                    LocationCode = x.Lot.LocationCode,
                    Quantity = x.Pick.Quantity,
                    UomId = x.Alternate?.BaseUomId ?? p.Material.BaseUomId,
                    UomCode = x.Alternate?.BaseUomCode ?? p.Material.BaseUomCode,
                    AlternateMaterialCode = x.Alternate?.MaterialCode,
                    Ratio = x.Alternate?.Ratio,
                    Free = x.Lot.Candidate.Free,
                    ExpiresAt = x.Lot.Candidate.ExpiresAt,
                    ReceivedAt = x.Lot.Candidate.ReceivedAt,
                    Reason = x.Pick.Reason,
                })],
                Excluded = [.. p.Excluded.Select(x => new LotExclusionDto
                {
                    LotId = x.Lot.Candidate.LotId,
                    LotLabel = x.Lot.Candidate.Label,
                    LocationCode = x.Lot.LocationCode,
                    Quantity = x.Lot.Candidate.Quantity,
                    Reason = x.Exclusion.Reason,
                })],
            })],
        };
    }
}

/// <summary>Locking and writing shared by the allocation commands: the job, then the lots in id order, then their allocations (D-027).</summary>
internal sealed class AllocationWriter(
    IInventoryRepository inventory, IAllocationRepository allocations, IUnitRepository units, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
{
    public async Task<JobAllocationSource> LockJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        _ = await inventory.GetJobForUpdateAsync(jobId, cancellationToken) ?? throw new NotFoundException($"Job {jobId} not found.");
        var source = await allocations.LoadAsync(jobId, cancellationToken) ?? throw new DomainRuleException("Only a production job's components are allocated.");
        if (!AllocationPlanner.CanAllocate(source.Job))
        {
            throw new DomainRuleException($"{source.Job.JobNumber} is {source.Job.Status}; nothing is allocated to it.");
        }

        return source;
    }

    public async Task<IReadOnlyList<ComponentPlan>> PlanAsync(JobAllocationSource source, CancellationToken cancellationToken) =>
        AllocationPlanner.Plan(
            source, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([.. source.Needs.Select(n => n.MaterialId).Distinct()], cancellationToken),
            time.GetUtcNow());

    /// <summary>Locks every lot the job's allocations and candidates name, then their allocations, and reads the job again.</summary>
    public async Task<JobAllocationSource> LockLotsAsync(JobAllocationSource source, CancellationToken cancellationToken)
    {
        var lotIds = source.Lots.Select(l => l.Candidate.LotId).Concat(source.Allocations.Select(a => a.Allocation.LotId)).Distinct().ToList();
        if (lotIds.Count > 0)
        {
            await inventory.LockLotsAsync(lotIds, cancellationToken);
            await allocations.LockActiveOfLotsAsync(lotIds, cancellationToken);
        }

        return (await allocations.LoadAsync(source.Job.Id, cancellationToken))!;
    }

    /// <summary>Allocates stock of a lot to the job, topping up its allocation of that lot; an approved alternate (#82) names the component it stands in for, and its ratio.</summary>
    public async Task AllocateAsync(
        JobAllocationSource source, Guid materialId, Guid lotId, decimal quantity, string basis, string? note, CancellationToken cancellationToken,
        Guid? forMaterialId = null, decimal? ratio = null)
    {
        var existing = source.Allocations.FirstOrDefault(a => a.Allocation.LotId == lotId)?.Allocation;
        var allocation = existing?.TopUp(quantity)
            ?? new LotAllocation(ids.NewId(), source.Job.SiteId, source.Job.Id, materialId, lotId, quantity, quantity, AllocationStatus.Active, basis, note,
                forMaterialId, ratio);
        var envelope = events.Create(EventTypes.LotAllocated, "lot", lotId,
            new
            {
                JobId = source.Job.Id,
                source.Job.JobNumber,
                AllocationId = allocation.Id,
                Quantity = quantity,
                QuantityAllocated = allocation.Quantity,
                Basis = basis,
                Note = note,
                ForMaterialId = forMaterialId,
                Ratio = ratio,
            });
        if (existing is null)
        {
            await allocations.InsertAsync(allocation, envelope.ActorId, envelope.OccurredAt, cancellationToken);
        }
        else
        {
            await allocations.UpdateAsync(allocation, envelope.ActorId, envelope.OccurredAt, cancellationToken);
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
    }

    public async Task ChangeAsync(LotAllocation before, LotAllocation after, string eventType, string? note, CancellationToken cancellationToken)
    {
        var envelope = events.Create(eventType, "lot", after.LotId,
            new { after.JobId, AllocationId = after.Id, QuantityBefore = before.Quantity, Quantity = after.Quantity, after.Status, Note = note });
        await allocations.UpdateAsync(after, envelope.ActorId, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// Releases allocations that are no longer usable, trims those a lot can no longer give, then (with <paramref name="topUp"/>) allocates
    /// what is still needed from the recommendations, for one component or all. The job must be locked.
    /// </summary>
    public async Task<AllocationChangeResponse> RefreshAsync(JobAllocationSource source, bool release, bool topUp, Guid? materialId, CancellationToken cancellationToken)
    {
        source = await LockLotsAsync(source, cancellationToken);
        int released = 0, trimmed = 0, allocated = 0;
        var notes = new List<string>();
        if (release)
        {
            foreach (var plan in await PlanAsync(source, cancellationToken))
            {
                foreach (var (held, lot, problem) in plan.Allocations.Where(a => a.Problem is not null))
                {
                    var current = held.Allocation;
                    var left = lot is null ? 0m : lot.Candidate.Quantity - lot.Candidate.AllocatedToOthers;
                    if (lot is not null && LotRecommendation.Problem(lot.Candidate, AllocationPlanner.NeededBy(source.Job, time.GetUtcNow())) is null && left > 0)
                    {
                        await ChangeAsync(current, current.TrimTo(left), EventTypes.LotAllocationChanged, problem, cancellationToken);
                        trimmed++;
                    }
                    else
                    {
                        await ChangeAsync(current, current.Release(problem), EventTypes.LotAllocationReleased, problem, cancellationToken);
                        released++;
                    }

                    notes.Add($"{source.Job.JobNumber}, {lot?.Candidate.Label ?? "a lot"}: {problem}");
                }
            }

            source = (await allocations.LoadAsync(source.Job.Id, cancellationToken))!;
        }

        if (topUp)
        {
            foreach (var plan in (await PlanAsync(source, cancellationToken)).Where(p => materialId is null || p.Material.MaterialId == materialId))
            {
                foreach (var (pick, lot, alternate) in plan.Picks)
                {
                    await AllocateAsync(source, lot.MaterialId, lot.Candidate.LotId, pick.Quantity, AllocationBasis.Recommended, pick.Reason, cancellationToken,
                        alternate?.ComponentId, alternate?.Ratio);
                    allocated++;
                }

                if (plan.Need is not null && plan.Remaining > plan.Suggested)
                {
                    notes.Add($"{source.Job.JobNumber}: {plan.Material.MaterialCode} is still {Qty(plan.Remaining - plan.Suggested)} {plan.Material.BaseUomCode} short of free stock.");
                }
            }
        }

        return new AllocationChangeResponse { Allocated = allocated, Released = released, Trimmed = trimmed, Jobs = 1, Notes = notes };
    }

    public static string Qty(decimal value) => InventoryRules.Qty(value);
}

internal sealed class AllocateLotHandler(
    IUnitOfWork unitOfWork, IInventoryRepository inventory, IAllocationRepository allocations, IUnitRepository units, IEventLog eventLog, IEventFactory events,
    IIdGenerator ids, TimeProvider time) : ICommandHandler<AllocateLotCommand, AllocationChangeResponse>
{
    public async Task<AllocationChangeResponse> HandleAsync(AllocateLotCommand command, CancellationToken cancellationToken)
    {
        var note = InventoryRules.Text(command.Note);
        new Validator()
            .Require(command.LotId != Guid.Empty, nameof(command.LotId), "Choose a lot.")
            .Require(command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .Require(note is null || note.Length <= 500, nameof(command.Note), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var writer = new AllocationWriter(inventory, allocations, units, eventLog, events, ids, time);
        var source = await writer.LockJobAsync(command.JobId, cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, null, cancellationToken);
        await allocations.LockActiveOfLotsAsync([lot.Id], cancellationToken);
        source = (await allocations.LoadAsync(command.JobId, cancellationToken))!;
        if (lot.SiteId != source.Job.SiteId)
        {
            throw new DomainRuleException($"{lot.Name} is in another site than {source.Job.JobNumber}.");
        }

        // A planned component, or an approved alternate for one (#82), the first by priority if it stands in for several.
        var alternate = source.Needs.Any(n => n.MaterialId == lot.MaterialId)
            ? null
            : (source.Alternates ?? []).Where(a => a.MaterialId == lot.MaterialId).OrderBy(a => a.Priority).FirstOrDefault()
              ?? throw new DomainRuleException($"{source.Job.JobNumber} doesn't plan to consume that material, and it isn't an approved alternate on its BOM.");

        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = await InventoryRules.ToBaseAsync(units, material, command.Quantity, command.UomId, cancellationToken);
        var candidate = source.Lots.FirstOrDefault(l => l.Candidate.LotId == lot.Id)?.Candidate
            ?? throw new DomainRuleException($"{lot.Name} isn't on hand.");
        if (LotRecommendation.Problem(candidate, AllocationPlanner.NeededBy(source.Job, time.GetUtcNow())) is { } problem)
        {
            throw new DomainRuleException($"{lot.Name} can't be allocated: {problem}");
        }

        if (quantity > candidate.Free)
        {
            throw new DomainRuleException(
                $"{lot.Name} has only {InventoryRules.Qty(candidate.Free)} {material.BaseUomCode} free" +
                (candidate.AllocatedToOthers > 0 ? $"; {InventoryRules.Qty(candidate.AllocatedToOthers)} {material.BaseUomCode} is allocated to other jobs." : "."));
        }

        await writer.AllocateAsync(source, lot.MaterialId, lot.Id, quantity, AllocationBasis.Chosen, note, cancellationToken, alternate?.ComponentId, alternate?.Ratio);
        await transaction.CommitAsync(cancellationToken);
        return new AllocationChangeResponse { Allocated = 1, Jobs = 1 };
    }
}

internal sealed class AllocateRecommendedHandler(
    IUnitOfWork unitOfWork, IInventoryRepository inventory, IAllocationRepository allocations, IUnitRepository units, IEventLog eventLog, IEventFactory events,
    IIdGenerator ids, TimeProvider time) : ICommandHandler<AllocateRecommendedCommand, AllocationChangeResponse>
{
    public async Task<AllocationChangeResponse> HandleAsync(AllocateRecommendedCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var writer = new AllocationWriter(inventory, allocations, units, eventLog, events, ids, time);
        var result = await writer.RefreshAsync(await writer.LockJobAsync(command.JobId, cancellationToken), release: false, topUp: true, command.MaterialId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

internal sealed class RefreshAllocationsHandler(
    IUnitOfWork unitOfWork, IInventoryRepository inventory, IAllocationRepository allocations, IUnitRepository units, IEventLog eventLog, IEventFactory events,
    IIdGenerator ids, TimeProvider time) : ICommandHandler<RefreshAllocationsCommand, AllocationChangeResponse>
{
    public async Task<AllocationChangeResponse> HandleAsync(RefreshAllocationsCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var writer = new AllocationWriter(inventory, allocations, units, eventLog, events, ids, time);
        var result = await writer.RefreshAsync(await writer.LockJobAsync(command.JobId, cancellationToken), release: true, topUp: true, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

internal sealed class RefreshSiteAllocationsHandler(
    IUnitOfWork unitOfWork, IInventoryRepository inventory, IAllocationRepository allocations, IUnitRepository units, IEventLog eventLog, IEventFactory events,
    IIdGenerator ids, TimeProvider time) : ICommandHandler<RefreshSiteAllocationsCommand, AllocationChangeResponse>
{
    public async Task<AllocationChangeResponse> HandleAsync(RefreshSiteAllocationsCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(command.SiteId != Guid.Empty, nameof(command.SiteId), "Required.").ThrowIfInvalid();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var writer = new AllocationWriter(inventory, allocations, units, eventLog, events, ids, time);

        // Every job first, in id order, then each job's lots: a declaration locks a job before its lots, so this can't deadlock with one.
        var jobIds = await allocations.ListOpenJobsAsync(command.SiteId, cancellationToken);
        var sources = new List<JobAllocationSource>();
        foreach (var id in jobIds)
        {
            sources.Add(await writer.LockJobAsync(id, cancellationToken));
        }

        int allocated = 0, released = 0, trimmed = 0;
        var notes = new List<string>();

        // Earliest need first, so the jobs that start soonest get the lots that expire soonest.
        var now = time.GetUtcNow();
        foreach (var source in sources.OrderBy(s => AllocationPlanner.NeededBy(s.Job, now)).ThenBy(s => s.Job.JobNumber, StringComparer.Ordinal))
        {
            var result = await writer.RefreshAsync((await allocations.LoadAsync(source.Job.Id, cancellationToken))!, release: true, topUp: true, null, cancellationToken);
            (allocated, released, trimmed) = (allocated + result.Allocated, released + result.Released, trimmed + result.Trimmed);
            notes.AddRange(result.Notes);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AllocationChangeResponse { Allocated = allocated, Released = released, Trimmed = trimmed, Jobs = sources.Count, Notes = notes };
    }
}

internal sealed class ReleaseAllocationHandler(
    IUnitOfWork unitOfWork, IInventoryRepository inventory, IAllocationRepository allocations, IUnitRepository units, IEventLog eventLog, IEventFactory events,
    IIdGenerator ids, TimeProvider time) : ICommandHandler<ReleaseAllocationCommand, AllocationChangeResponse>
{
    public async Task<AllocationChangeResponse> HandleAsync(ReleaseAllocationCommand command, CancellationToken cancellationToken)
    {
        var note = InventoryRules.Text(command.Note);
        new Validator().Require(note is null || note.Length <= 500, nameof(command.Note), "At most 500 characters.").ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var found = await allocations.GetAsync(command.AllocationId, cancellationToken);
        if (found is null || found.JobId != command.JobId)
        {
            throw new NotFoundException($"Allocation {command.AllocationId} of that job wasn't found.");
        }

        _ = await inventory.GetJobForUpdateAsync(found.JobId, cancellationToken);
        await inventory.LockLotsAsync([found.LotId], cancellationToken);
        var held = (await allocations.LockActiveOfLotsAsync([found.LotId], cancellationToken)).FirstOrDefault(a => a.Allocation.Id == found.Id)?.Allocation
            ?? throw new DomainRuleException($"That allocation is {found.Status} already.");
        var writer = new AllocationWriter(inventory, allocations, units, eventLog, events, ids, time);
        await writer.ChangeAsync(held, held.Release(note), EventTypes.LotAllocationReleased, note, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AllocationChangeResponse { Released = 1, Jobs = 1 };
    }
}

/// <summary>What consumption does to allocations (D-064): its own job's allocation of the lot is used up; eating into other jobs' warns.</summary>
internal sealed class GetAllocationSettingsHandler(IAllocationReadStore store) : IQueryHandler<GetAllocationSettingsQuery, AllocationSettingsDto>
{
    public async Task<AllocationSettingsDto> HandleAsync(GetAllocationSettingsQuery query, CancellationToken cancellationToken)
    {
        var (settings, version) = await store.GetSettingsAsync(query.SiteId, cancellationToken);
        return new AllocationSettingsDto
        {
            SiteId = query.SiteId,
            KeepCurrent = settings.KeepCurrent,
            HardReservation = settings.HardReservation,
            Preferences = settings.Preferences,
            RefreshedAt = settings.RefreshedAt,
            Version = version,
        };
    }
}

internal sealed class SetAllocationSettingsHandler(IUnitOfWork unitOfWork, IAllocationRepository allocations, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetAllocationSettingsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetAllocationSettingsCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Preferences.All(AllocationPreferences.All.Contains), nameof(command.Preferences), $"Each one of {string.Join(", ", AllocationPreferences.All)}.")
            .Require(command.Preferences.Distinct().Count() == command.Preferences.Count, nameof(command.Preferences), "Each preference at most once.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await allocations.GetSettingsForUpdateAsync(command.SiteId, cancellationToken);
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("The site's allocation settings were changed by someone else. Reload and try again.");
        }

        var updated = current.Value with { KeepCurrent = command.KeepCurrent, HardReservation = command.HardReservation, Preferences = [.. command.Preferences] };
        var envelope = events.Create(EventTypes.AllocationSettingsSet, "site", command.SiteId, new { Before = current.Value, After = updated });
        var version = await allocations.UpdateSettingsAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class GetPickingListHandler(IAllocationReadStore store) : IQueryHandler<GetPickingListQuery, PickingListDto>
{
    public Task<PickingListDto> HandleAsync(GetPickingListQuery query, CancellationToken cancellationToken) =>
        store.GetPickingListAsync(query.SiteId, query.JobId, query.LineId, cancellationToken);
}

/// <summary>
/// Keeps allocations current (#82) for sites that ask: each site whose stock or schedule changed since its last refresh, once the changes
/// have settled, is refreshed whole (<see cref="RefreshSiteAllocationsCommand"/>, earliest need first). Run by a background service.
/// </summary>
public sealed class AllocationAutoRefresh(
    IUnitOfWork unitOfWork,
    IAllocationRepository allocations,
    ICommandHandler<RefreshSiteAllocationsCommand, AllocationChangeResponse> refresh,
    TimeProvider time,
    ILogger<AllocationAutoRefresh>? logger = null)
{
    /// <returns>The sites refreshed.</returns>
    public async Task<int> RefreshDueAsync(TimeSpan quiet, TimeSpan atMost, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> due;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            due = await allocations.ListSitesDueForRefreshAsync(quiet, atMost, cancellationToken);
        }

        foreach (var siteId in due)
        {
            // Changes made while it runs come after this time, so they bring the next refresh.
            var started = time.GetUtcNow();
            try
            {
                await refresh.HandleAsync(new RefreshSiteAllocationsCommand(siteId), cancellationToken);
            }
            catch (Exception ex) when (ex is DomainRuleException or ConflictException or NotFoundException)
            {
                // Marked anyway, so one site's problem is logged once per change, not every round.
                logger?.LogWarning(ex, "Couldn't refresh the allocations of site {SiteId}.", siteId);
            }

            await using var marking = await unitOfWork.BeginAsync(cancellationToken);
            await allocations.MarkRefreshedAsync(siteId, started, cancellationToken);
            await marking.CommitAsync(cancellationToken);
        }

        return due.Count;
    }
}

internal static class AllocationUse
{
    /// <summary>
    /// Runs after the job and lot are locked, before the lot changes. Taking what other jobs hold is warned about; under the site's hard
    /// reservation (#82) it is refused, naming them: whoever allocates releases or moves their allocation first.
    /// </summary>
    public static async Task<(decimal Used, InventoryWarningDto? Warning)> ConsumeAsync(
        IAllocationRepository allocations, Lot lot, Guid jobId, decimal quantity, string actor, DateTimeOffset at, string unitCode, CancellationToken cancellationToken)
    {
        var hard = (await allocations.GetSettingsAsync(lot.SiteId, cancellationToken)).HardReservation;
        var held = await allocations.LockActiveOfLotsAsync([lot.Id], cancellationToken);
        var used = 0m;
        if (held.FirstOrDefault(a => a.Allocation.JobId == jobId) is { } own)
        {
            var (after, take) = own.Allocation.UseUp(quantity);
            await allocations.UpdateAsync(after, actor, at, cancellationToken);
            used = take;
        }

        var others = held.Where(a => a.Allocation.JobId != jobId).ToList();
        var free = lot.Quantity - others.Sum(a => a.Allocation.Quantity);
        if (hard && others.Count > 0 && quantity > free)
        {
            throw new DomainRuleException(
                $"{InventoryRules.Qty(quantity - Math.Max(0m, free))} {unitCode} of {lot.Name} is reserved for {string.Join(", ", others.Select(o => o.JobNumber).Distinct())}; " +
                "release or move their allocation first.");
        }

        return others.Count > 0 && quantity > free
            ? (used, new InventoryWarningDto("allocated_elsewhere",
                $"{InventoryRules.Qty(quantity - Math.Max(0m, free))} {unitCode} of it was allocated to {string.Join(", ", others.Select(o => o.JobNumber).Distinct())}; it was consumed anyway. Refresh their allocations."))
            : (used, null);
    }
}
