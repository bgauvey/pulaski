using Pulaski.Application.Abstractions;
using Pulaski.Application.Units;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Automated consumption at feed points (#33, D-079), inside the caller's transaction with the job locked first (D-027). A production report
/// backflushes its components from the lots at the line's feed points, and shares its time-window components among the lots loaded while
/// it was made; a feed point's consumed counter takes each count out of the lot loaded then. Each is an ordinary consumption, attributed by
/// <c>auto_source</c> and <c>source_ref</c>, never below zero; what it can't cover is kept as a shortfall to resolve or dismiss.
/// </summary>
internal sealed class FeedAutomation(
    IInventoryRepository inventory, IUnitRepository units, IFeedRepository feeds, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
{
    /// <summary>
    /// After a report's counts and lots: its backflush and time-window components, from <paramref name="output"/> (good and scrap, in the job's
    /// unit) at <paramref name="at"/>, when the report counts. Returns warnings for what fell short.
    /// </summary>
    public async Task<IReadOnlyList<InventoryWarningDto>> OnReportAsync(
        InventoryJob job, JobRun run, Guid reportId, decimal output, DateTimeOffset at, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var components = await feeds.ListAutoComponentsAsync(job.Id, cancellationToken);
        if (components.Count == 0 || output <= 0 || job.PlannedQty is not > 0)
        {
            return [];
        }

        var feedPoints = await feeds.ListFeedPointsOfLineAsync(run.EquipmentId, cancellationToken);
        var loads = feedPoints.Count == 0 ? [] : await LoadsAsync(feedPoints.Select(f => f.Id).ToList(), at, cancellationToken);
        var from = await feeds.GetPreviousReportAtAsync(run.Id, reportId, at, cancellationToken) ?? run.StartedAt;
        var shorts = new Shortfalls(job, run.Id, reportId, at);
        var allUnits = await units.GetUnitsAsync(cancellationToken);
        foreach (var component in components)
        {
            var material = await InventoryRules.MaterialAsync(inventory, component.MaterialId, cancellationToken);
            var source = component.Method == ConsumptionMethod.Backflush ? AutoSource.Backflush : AutoSource.TimeWindow;
            var factor = UnitConversions.For(material.Id, allUnits, await units.GetConversionsAsync([material.Id], cancellationToken)).Factor(component.UomId, material.BaseUomId);
            if (factor is not { } f)
            {
                shorts.Add(run.EquipmentId, material, null, component.PlannedQty * output / job.PlannedQty.Value, source,
                    $"The component's unit doesn't convert to {material.BaseUomCode}, so nothing was consumed.", component.UomId);
                continue;
            }

            var quantity = Math.Round(component.PlannedQty * output / job.PlannedQty.Value * f, 6, MidpointRounding.AwayFromZero);
            if (quantity <= 0)
            {
                continue;
            }

            if (source == AutoSource.Backflush)
            {
                // Lots loaded at the line's feed points first, then the other lots there, first expiry first.
                var loaded = loads.Where(l => l.MaterialId == material.Id && l.Covers(at)).OrderByDescending(l => l.LoadedAt).Select(l => l.LotId);
                var there = await feeds.ListLotsAtAsync([.. feedPoints.Select(p => p.Id)], material.Id, cancellationToken);
                var order = loaded.Concat(there).Distinct().ToList();
                var left = quantity;
                foreach (var lotId in order)
                {
                    left -= await TakeAsync(job, run.Id, lotId, left, source, reportId, at, now, cancellationToken);
                    if (left <= 0)
                    {
                        break;
                    }
                }

                if (left > 0)
                {
                    shorts.Add(run.EquipmentId, material, null, left, source,
                        order.Count == 0 ? $"No lot of {material.Code} was at {job.LineCode}'s feed points." : $"The lots of {material.Code} at {job.LineCode}'s feed points didn't hold enough.");
                }

                continue;
            }

            // Time window: shared among what was loaded while it was made, at feed points without a counter (those count it themselves).
            var counted = feedPoints.Where(p => p.HasCounter).Select(p => p.Id).ToHashSet();
            var shares = FeedLoads.ShareByTime(loads.Where(l => !counted.Contains(l.FeedPointId)), material.Id, from, at, quantity);
            if (shares.Count == 0)
            {
                if (FeedLoads.ShareByTime(loads.Where(l => counted.Contains(l.FeedPointId)), material.Id, from, at, quantity).Count == 0)
                {
                    shorts.Add(run.EquipmentId, material, null, quantity, source, $"Nothing of {material.Code} was loaded at {job.LineCode}'s feed points while it was made.");
                }

                continue;
            }

            foreach (var share in shares)
            {
                var taken = await TakeAsync(job, run.Id, share.LotId, share.Quantity, source, reportId, at, now, cancellationToken);
                if (taken < share.Quantity)
                {
                    var feedPoint = loads.First(l => l.LotId == share.LotId).FeedPointId;
                    shorts.Add(feedPoint, material, share.LotId, share.Quantity - taken, source, "The lot loaded then didn't hold enough, or couldn't be used.");
                }
            }
        }

        return await RecordAsync(shorts, cancellationToken);
    }

    /// <summary>One machine count at a feed point, out of the lot loaded then; in the caller's transaction. False when it was already posted.</summary>
    public async Task<bool> PostCountAsync(CounterRow count, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = await feeds.GetCounterContextAsync(count.FeedPointId, count.OccurredAt, cancellationToken);
        if (context is null)
        {
            return false;
        }

        // The job first, then the lot (D-027); then check, under the job's lock, that no one posted it meanwhile.
        var job = context.JobId is { } jobId ? await inventory.GetJobForUpdateAsync(jobId, cancellationToken) : null;
        if (await feeds.IsPostedAsync(count.Id, cancellationToken))
        {
            return false;
        }

        // A counter counts one material: with lots of two loaded it can't say which, so that is left for someone to decide.
        var loaded = (await LoadsAsync([count.FeedPointId], count.OccurredAt, cancellationToken)).Where(l => l.Covers(count.OccurredAt)).ToList();
        var load = loaded.Select(l => l.MaterialId).Distinct().Count() == 1 ? loaded[^1] : null;
        var material = load is null ? null : await InventoryRules.MaterialAsync(inventory, load.MaterialId, cancellationToken);
        var shorts = new Shortfalls(job, context.RunId, count.Id, count.OccurredAt, context.SiteId);
        if (job is null || material is null || load is null)
        {
            shorts.Add(count.FeedPointId, material, load?.LotId, count.Quantity, AutoSource.Counter,
                job is null ? $"Nothing was running on {context.FeedPointCode}'s line when it was counted."
                : loaded.Count == 0 ? $"Nothing was loaded at {context.FeedPointCode} when it was counted."
                : $"Lots of more than one material were loaded at {context.FeedPointCode}, so its counter can't say which it counted.",
                context.CounterUomId ?? material?.BaseUomId);
            await RecordAsync(shorts, cancellationToken);
            return true;
        }

        var allUnits = await units.GetUnitsAsync(cancellationToken);
        var factor = context.CounterUomId is { } counterUom
            ? UnitConversions.For(material.Id, allUnits, await units.GetConversionsAsync([material.Id], cancellationToken)).Factor(counterUom, material.BaseUomId)
            : 1m;
        if (factor is not { } f)
        {
            shorts.Add(count.FeedPointId, material, load.LotId, count.Quantity, AutoSource.Counter,
                $"The counter's unit doesn't convert to {material.BaseUomCode}, so nothing was consumed.", context.CounterUomId);
            await RecordAsync(shorts, cancellationToken);
            return true;
        }

        var quantity = Math.Round(count.Quantity * f, 6, MidpointRounding.AwayFromZero);
        var taken = quantity > 0 ? await TakeAsync(job, context.RunId, load.LotId, quantity, AutoSource.Counter, count.Id, count.OccurredAt, now, cancellationToken) : 0m;
        if (taken < quantity)
        {
            shorts.Add(count.FeedPointId, material, load.LotId, quantity - taken, AutoSource.Counter, "The lot loaded then didn't hold enough, or couldn't be used.");
        }

        await RecordAsync(shorts, cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<FeedLoad>> LoadsAsync(IReadOnlyList<Guid> feedPointIds, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var events = await feeds.ListFeedEventsAsync(feedPointIds, until, cancellationToken);
        return [.. feedPointIds.SelectMany(id => FeedLoads.From(id, events.Where(e => e.FromLocationId == id || e.ToLocationId == id)))];
    }

    /// <summary>Consumes up to <paramref name="wanted"/> from the lot into the job, as much as it can give then; returns what it took.</summary>
    private async Task<decimal> TakeAsync(
        InventoryJob job, Guid? runId, Guid lotId, decimal wanted, string source, Guid sourceRef, DateTimeOffset at, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await inventory.GetLotForUpdateAsync(lotId, cancellationToken) is not { Value: var lot }
            || lot.Status != LotStatus.Available || lot.SiteId != job.SiteId || lot.Quantity <= 0 || lot.ExpiresAt is { } expires && expires <= at)
        {
            return 0m;
        }

        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = Math.Min(wanted, lot.Quantity);
        try
        {
            await Backdating.EnsureFitsAsync(inventory, lot, -quantity, at, now, material.BaseUomCode, cancellationToken);
        }
        catch (DomainRuleException)
        {
            return 0m;
        }

        var backdated = MovementTime.Backdated(at, now);
        var (after, movement) = lot.Take(quantity, LotEventKind.Consumed, material.BaseUomCode);
        var edges = InventoryRules.Edges(ids, [lot.Id], await inventory.ListLotsProducedByJobAsync(job.Id, cancellationToken), job.Id);
        var envelope = events.Create(
            EventTypes.LotConsumed, "lot", lot.Id,
            new
            {
                lot.LotNumber,
                JobId = job.Id,
                job.JobNumber,
                Quantity = quantity,
                QuantityAfter = after.Quantity,
                ChildLotIds = edges.Select(e => e.ChildLotId),
                RunId = runId,
                EffectiveAt = backdated,
                AutoSource = source,
                SourceRef = sourceRef,
            });
        await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, JobId: job.Id, RunId: runId, EffectiveAt: backdated,
                AutoSource: source, SourceRef: sourceRef),
            envelope.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync(edges, envelope.OccurredAt, cancellationToken, backdated);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return quantity;
    }

    private async Task<IReadOnlyList<InventoryWarningDto>> RecordAsync(Shortfalls shorts, CancellationToken cancellationToken)
    {
        var warnings = new List<InventoryWarningDto>();
        foreach (var item in shorts.Items)
        {
            var shortfall = item with { Id = ids.NewId(), Quantity = Math.Round(item.Quantity, 6, MidpointRounding.AwayFromZero) };
            var envelope = events.Create(EventTypes.FeedShortfallRecorded, "feed_shortfall", shortfall.Id, shortfall);
            await feeds.InsertShortfallAsync(shortfall, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            warnings.Add(new("feed_shortfall", $"{InventoryRules.Qty(shortfall.Quantity)} wasn't consumed automatically: {shortfall.Reason} It is listed under Inventory → Feed points."));
        }

        return warnings;
    }

    /// <summary>What a source fell short by, added up per place and material, then recorded once each.</summary>
    private sealed class Shortfalls(InventoryJob? job, Guid? runId, Guid sourceRef, DateTimeOffset at, Guid? siteId = null)
    {
        private readonly Dictionary<(Guid Equipment, Guid? Material), FeedShortfall> _items = [];

        public void Add(Guid equipmentId, InventoryMaterial? material, Guid? lotId, decimal quantity, string source, string reason, Guid? uomId = null)
        {
            var key = (equipmentId, material?.Id);
            _items[key] = _items.TryGetValue(key, out var had)
                ? had with { Quantity = had.Quantity + quantity }
                : new FeedShortfall(Guid.Empty, siteId ?? job!.SiteId, equipmentId, job?.Id, job is null ? null : runId, material?.Id, lotId,
                    quantity, uomId ?? material?.BaseUomId, source, sourceRef, at, reason);
        }

        public IEnumerable<FeedShortfall> Items => _items.Values.Where(i => i.Quantity > 0);
    }
}
