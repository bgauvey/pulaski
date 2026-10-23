using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>
/// Applies a batch of signals an edge node forwarded (D-050), in sequence order, once: signals of the same stream at or below
/// the last applied sequence are skipped, so a batch sent again changes nothing. Telemetry, so no events (D-005).
/// </summary>
public sealed record IngestEdgeSignalsCommand(Guid EdgeNodeId, Guid StreamId, string? AgentVersion, IReadOnlyList<EdgeSignalDto> Signals)
    : ICommand<EdgeSignalBatchResponse>;

/// <summary>Records the addresses a node's connections have seen, so tags can be bound by picking them.</summary>
public sealed record ReportEdgeMetricsCommand(Guid EdgeNodeId, IReadOnlyList<EdgeMetricReportDto> Metrics) : ICommand<int>;

internal sealed class IngestEdgeSignalsHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IRequestContext request, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<IngestEdgeSignalsCommand, EdgeSignalBatchResponse>
{
    public const int MaxSignals = 5000;

    public async Task<EdgeSignalBatchResponse> HandleAsync(IngestEdgeSignalsCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.StreamId != Guid.Empty, "StreamId", "Required.")
            .Require(command.Signals.Count <= MaxSignals, "Signals", $"At most {MaxSignals} at a time.")
            .Require(command.AgentVersion is null || command.AgentVersion.Length <= 50, "AgentVersion", "At most 50 characters.")
            .ThrowIfInvalid();

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var node = await EdgeRules.LockNodeAsync(edge, command.EdgeNodeId, null, cancellationToken);
        if (!node.IsActive)
        {
            throw new DomainRuleException($"{node.Code} is switched off; switch it on to take its data.");
        }

        var fresh = command.Signals
            .Where(s => EdgeStreams.IsNew(node.StreamId, node.LastSequence, command.StreamId, s.Sequence))
            .OrderBy(s => s.Sequence)
            .ToList();
        var lastSequence = node.StreamId == command.StreamId ? node.LastSequence : 0;
        if (fresh.Count > 0)
        {
            lastSequence = Math.Max(lastSequence, fresh[^1].Sequence);
        }

        var applied = await ApplyAsync(node, fresh, cancellationToken);
        await edge.UpdateNodeAsync(
            node with { StreamId = command.StreamId, LastSequence = lastSequence, LastSeenAt = now, AgentVersion = command.AgentVersion ?? node.AgentVersion },
            now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EdgeSignalBatchResponse { LastSequence = lastSequence, Applied = applied, Skipped = command.Signals.Count - applied };
    }

    private async Task<int> ApplyAsync(EdgeNodeRecord node, List<EdgeSignalDto> signals, CancellationToken cancellationToken)
    {
        if (signals.Count == 0)
        {
            return 0;
        }

        var tags = (await edge.ListIngestTagsAsync(node.Id, [.. signals.Select(s => s.TagId).Distinct()], cancellationToken)).ToDictionary(t => t.TagId);
        var equipmentIds = tags.Values.Select(t => t.EquipmentId).Distinct().ToList();
        if (equipmentIds.Count == 0)
        {
            return 0;
        }

        await edge.LockEquipmentAsync(equipmentIds, cancellationToken);
        var live = (await edge.GetLiveAsync(equipmentIds, cancellationToken)).ToDictionary(l => l.Key, l => (MachineLive?)l.Value);
        var counters = (await edge.GetCountersAsync(equipmentIds, cancellationToken)).ToDictionary(c => c.Key, c => (long?)c.Value);
        var maps = await edge.GetStateMapsAsync([.. tags.Values.Where(t => t.MachineProfileId is not null).Select(t => t.MachineProfileId!.Value).Distinct()], cancellationToken);
        var runs = (await edge.GetOpenRunsAsync(equipmentIds, cancellationToken)).ToDictionary(r => r.EquipmentId);
        var stopContexts = await edge.GetStopContextsAsync(equipmentIds, cancellationToken);
        var openStops = equipmentIds.ToDictionary(id => id, id => stopContexts.GetValueOrDefault(id)?.Open);
        var touchedStops = new Dictionary<Guid, (MachineStop Stop, bool IsNew)>();
        void Track(MachineStop? stop, bool isNew)
        {
            if (stop is not null)
            {
                touchedStops[stop.Id] = (stop, isNew || (touchedStops.TryGetValue(stop.Id, out var seen) && seen.IsNew));
            }
        }

        void Stops(Guid equipment, StateChange change, Guid? runId)
        {
            var context = stopContexts.GetValueOrDefault(equipment);
            var before = openStops[equipment];
            var (open, closed) = StopRules.Apply(before, change, equipment, runId, context?.Reasons ?? StopReasons.None,
                context?.MicroStop ?? MachineProfileRules.DefaultMicroStop, ids.NewId);
            Track(closed, isNew: false);
            Track(open is not null && open != before ? open : null, isNew: before is null);
            openStops[equipment] = open;
        }

        var changes = new List<StateChangeRow>();
        var counts = new List<MachineCountRow>();
        var changedLive = new HashSet<Guid>();
        var changedCounters = new Dictionary<(Guid, string), (long Value, DateTimeOffset At)>();
        var applied = 0;
        foreach (var signal in signals)
        {
            if (!tags.TryGetValue(signal.TagId, out var tag))
            {
                continue;
            }

            applied++;
            var equipment = tag.EquipmentId;
            var current = live.GetValueOrDefault(equipment);
            var run = runs.GetValueOrDefault(equipment);
            switch (tag.Role)
            {
                case TagRoles.State:
                    var raw = signal.Value is { } v ? (int?)decimal.ToInt32(decimal.Truncate(v)) : null;
                    var lost = !signal.Good || raw is null;
                    var packml = lost ? PackMlStates.Undefined : Meaning(raw!.Value, tag.MachineProfileId, maps);
                    var (state, change) = LiveStateRules.ApplyState(current, raw, packml, lost, signal.OccurredAt);
                    live[equipment] = state;
                    changedLive.Add(equipment);
                    if (change is not null)
                    {
                        changes.Add(new StateChangeRow(ids.NewId(), equipment, run?.RunId, signal.Sequence, change));
                        Stops(equipment, change, run?.RunId);
                    }

                    break;
                case TagRoles.UnitMode when signal.Good && signal.Value is { } mode:
                    var (withMode, modeChange) = LiveStateRules.ApplyUnitMode(current, decimal.ToInt32(decimal.Truncate(mode)), signal.OccurredAt);
                    live[equipment] = withMode;
                    changedLive.Add(equipment);
                    if (modeChange is not null)
                    {
                        changes.Add(new StateChangeRow(ids.NewId(), equipment, run?.RunId, signal.Sequence, modeChange));
                        Stops(equipment, modeChange, run?.RunId);
                    }

                    break;
                case TagRoles.StopReason when signal.Good && signal.Value is { } code:
                    var stopCode = decimal.ToInt32(decimal.Truncate(code));
                    live[equipment] = LiveStateRules.ApplyStopReason(current, stopCode, signal.OccurredAt);
                    changedLive.Add(equipment);
                    if (openStops[equipment] is { } stopped)
                    {
                        var coded = StopRules.WithStopCode(stopped, stopCode, stopContexts.GetValueOrDefault(equipment)?.Reasons ?? StopReasons.None);
                        if (coded != stopped)
                        {
                            openStops[equipment] = coded;
                            Track(coded, isNew: false);
                        }
                    }

                    break;
                case TagRoles.MachineSpeed when signal.Good && signal.Value is { } speed:
                    live[equipment] = LiveStateRules.ApplySpeed(current, speed * tag.ScaleFactor + tag.ScaleOffset, signal.OccurredAt);
                    changedLive.Add(equipment);
                    break;
                case var role when TagRoles.CountKind(role) is { } kind && signal.Good && signal.Value is { } reading:
                    var value = decimal.ToInt64(decimal.Truncate(reading));
                    var key = (equipment, kind);
                    var delta = Counters.Delta(counters.GetValueOrDefault(key), value, tag.CounterRollover);
                    counters[key] = value;
                    changedCounters[key] = (value, signal.OccurredAt);
                    if (delta > 0)
                    {
                        counts.Add(new MachineCountRow(ids.NewId(), signal.OccurredAt, equipment, run?.RunId, run?.JobId, kind, delta * tag.ScaleFactor, value));
                    }

                    break;
            }
        }

        await edge.InsertStateChangesAsync(changes, cancellationToken);
        await edge.InsertCountsAsync(counts, request.ActorId, cancellationToken);
        await edge.InsertStopsAsync([.. touchedStops.Values.Where(t => t.IsNew).Select(t => t.Stop)], cancellationToken);
        await edge.UpdateStopsAsync([.. touchedStops.Values.Where(t => !t.IsNew).Select(t => t.Stop)], cancellationToken);
        foreach (var equipment in changedLive)
        {
            await edge.UpsertLiveAsync(equipment, live[equipment]!, openStops[equipment], node.Id, time.GetUtcNow(), cancellationToken);
        }

        foreach (var ((equipment, kind), (value, at)) in changedCounters)
        {
            await edge.UpsertCounterAsync(equipment, kind, value, at, cancellationToken);
        }

        return applied;
    }

    /// <summary>The PackML state a raw value means: by the machine's profile, or PackML's own numbers without one.</summary>
    private static int Meaning(int raw, Guid? profileId, IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, int>> maps)
    {
        if (profileId is { } id && maps.TryGetValue(id, out var map))
        {
            return map.TryGetValue(raw, out var state) ? state : PackMlStates.Undefined;
        }

        return PackMlStates.IsValid(raw) ? raw : PackMlStates.Undefined;
    }
}

internal sealed class ReportEdgeMetricsHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, TimeProvider time) : ICommandHandler<ReportEdgeMetricsCommand, int>
{
    public const int MaxMetrics = 5000;

    public async Task<int> HandleAsync(ReportEdgeMetricsCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Metrics.Count <= MaxMetrics, "Metrics", $"At most {MaxMetrics} at a time.")
            .Require(command.Metrics.All(m => m.Address.Length is > 0 and <= 400), "Metrics", "Each address is 1 to 400 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var node = await EdgeRules.LockNodeAsync(edge, command.EdgeNodeId, null, cancellationToken);
        var own = (await edge.ListNodeConnectionsAsync(node.Id, [.. command.Metrics.Select(m => m.ConnectionId).Distinct()], cancellationToken)).ToHashSet();
        var metrics = command.Metrics
            .Where(m => own.Contains(m.ConnectionId))
            .GroupBy(m => (m.ConnectionId, m.Address))
            .Select(g => g.MaxBy(m => m.SeenAt)! with { Value = Truncate(g.MaxBy(m => m.SeenAt)!.Value), DataType = Truncate(g.First().DataType) })
            .ToList();
        await edge.UpsertMetricsAsync(metrics, cancellationToken);
        // Reporting what it sees is hearing from it, even while nothing bound changes.
        var now = time.GetUtcNow();
        await edge.UpdateNodeAsync(node with { LastSeenAt = now }, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return metrics.Count;
    }

    private static string? Truncate(string? value) => value is { Length: > 200 } ? value[..200] : value;
}
