using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>
/// What each piece of equipment's time was used for over a period (D-052): classified from its own state history and stops, or
/// combined from its machines' as it derives its state (D-050). Shared by the OEE report and the Overview (D-070).
/// </summary>
internal sealed class EquipmentUses
{
    private const int MaxDepth = 16;

    private readonly Dictionary<Guid, OeeEquipmentRecord> _byId;
    private readonly ILookup<Guid, OeeEquipmentRecord> _children;
    private readonly ILookup<Guid, StatePoint> _states;
    private readonly ILookup<Guid, StopSpan> _stops;
    private readonly IReadOnlySet<int> _modes;
    private readonly DateTimeOffset _start;
    private readonly DateTimeOffset _end;
    private readonly Dictionary<Guid, IReadOnlyList<UseSpan>> _uses = [];

    private EquipmentUses(
        IReadOnlyList<OeeEquipmentRecord> equipment, ILookup<Guid, StatePoint> states, ILookup<Guid, StopSpan> stops, IReadOnlySet<int> modes,
        DateTimeOffset start, DateTimeOffset end)
    {
        _byId = equipment.ToDictionary(e => e.Id);
        _children = equipment.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
        _states = states;
        _stops = stops;
        _modes = modes;
        _start = start;
        _end = end;
    }

    /// <summary>
    /// Loads the history of <paramref name="shown"/> (some of <paramref name="equipment"/>: the site's equipment that tracks state, in
    /// tree order) for <c>[start, end)</c>, and of everything their time comes from.
    /// </summary>
    public static async Task<EquipmentUses> LoadAsync(
        IOeeReadStore store, Guid siteId, IReadOnlyList<OeeEquipmentRecord> equipment, IEnumerable<Guid> shown, DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var tree = new EquipmentUses(equipment, Enumerable.Empty<(Guid, StatePoint)>().ToLookup(x => x.Item1, x => x.Item2),
            Enumerable.Empty<(Guid, StopSpan)>().ToLookup(x => x.Item1, x => x.Item2), new HashSet<int>(), start, end);
        var needed = new HashSet<Guid>();
        foreach (var id in shown)
        {
            tree.Need(id, 0, needed);
        }

        var modes = await store.GetProductionModesAsync(siteId, cancellationToken);
        var states = (await store.ListStatesAsync(needed, start, end, cancellationToken)).ToLookup(s => s.EquipmentId, s => s.State);
        var stops = (await store.ListStopsAsync(needed, start, end, cancellationToken)).ToLookup(s => s.EquipmentId, s => s.Stop);
        return new EquipmentUses(equipment, states, stops, modes, start, end);
    }

    public bool Tracks(Guid id) => _byId.ContainsKey(id);

    /// <summary>The equipment's time over the period, classified; empty for equipment that doesn't track state.</summary>
    public IReadOnlyList<UseSpan> Of(Guid id) => _byId.ContainsKey(id) ? Uses(id, 0) : [];

    /// <summary>The stops of the machines the equipment's time comes from (itself, when it has its own tags).</summary>
    public IReadOnlyList<(Guid EquipmentId, StopSpan Stop)> StopsOf(Guid id)
    {
        if (!_byId.ContainsKey(id))
        {
            return [];
        }

        var sources = new HashSet<Guid>();
        Sources(id, 0, sources);
        return [.. sources.SelectMany(s => _stops[s].Select(stop => (s, stop)))];
    }

    // Lines take their time from their machines, and those perhaps from theirs: everything that feeds what is shown.
    private void Need(Guid id, int depth, HashSet<Guid> needed)
    {
        if (!needed.Add(id) || depth >= MaxDepth)
        {
            return;
        }

        var e = _byId[id];
        if (e.StateDerivation is StateDerivations.Series or StateDerivations.Parallel)
        {
            foreach (var child in _children[id])
            {
                Need(child.Id, depth + 1, needed);
            }
        }
        else if (e.StateDerivation == StateDerivations.ConstraintMachine && e.ConstraintEquipmentId is { } constraint && _byId.ContainsKey(constraint))
        {
            Need(constraint, depth + 1, needed);
        }
    }

    // The equipment whose own history is classified, as Uses walks the tree.
    private void Sources(Guid id, int depth, HashSet<Guid> sources)
    {
        var e = _byId[id];
        if (depth < MaxDepth && e.StateDerivation is StateDerivations.Series or StateDerivations.Parallel)
        {
            foreach (var child in _children[id])
            {
                Sources(child.Id, depth + 1, sources);
            }
        }
        else if (depth < MaxDepth && e.StateDerivation == StateDerivations.ConstraintMachine && e.ConstraintEquipmentId is { } constraint && _byId.ContainsKey(constraint))
        {
            Sources(constraint, depth + 1, sources);
        }
        else
        {
            sources.Add(id);
        }
    }

    private IReadOnlyList<UseSpan> Uses(Guid id, int depth)
    {
        if (_uses.TryGetValue(id, out var done))
        {
            return done;
        }

        var e = _byId[id];
        IReadOnlyList<UseSpan> result;
        if (depth < MaxDepth && e.StateDerivation is StateDerivations.Series or StateDerivations.Parallel)
        {
            result = Utilization.Combine(e.StateDerivation, [.. _children[id].Select(c => Uses(c.Id, depth + 1))], _start, _end);
        }
        else if (depth < MaxDepth && e.StateDerivation == StateDerivations.ConstraintMachine && e.ConstraintEquipmentId is { } constraint && _byId.ContainsKey(constraint))
        {
            result = Uses(constraint, depth + 1);
        }
        else
        {
            result = Utilization.Classify([.. _states[id]], [.. _stops[id]], _modes, _start, _end);
        }

        _uses[id] = result;
        return result;
    }
}
