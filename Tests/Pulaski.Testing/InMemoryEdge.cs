using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Testing;

/// <summary>In-memory edge nodes, connections, tags and machine data (D-050). Sites, equipment, profiles and runs are added by the test.</summary>
public sealed class InMemoryEdge : IEdgeRepository, IEdgeReadStore
{
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _sites = [];
    private readonly Dictionary<Guid, Versioned<EdgeNodeRecord>> _nodes = [];
    private readonly Dictionary<Guid, Versioned<EdgeConnectionRecord>> _connections = [];
    private readonly Dictionary<Guid, Versioned<EquipmentTagRecord>> _tags = [];
    private readonly Dictionary<Guid, (TagEquipment Equipment, Guid? ProfileId)> _equipment = [];
    private readonly Dictionary<Guid, Dictionary<int, int>> _maps = [];
    private readonly Dictionary<Guid, OpenRun> _runs = [];
    private readonly Dictionary<Guid, MachineLive> _live = [];
    private readonly Dictionary<(Guid, string), long> _counters = [];
    private readonly Dictionary<(Guid, string), EdgeMetricReportDto> _metrics = [];
    private readonly Dictionary<Guid, Guid?> _openStops = [];
    private readonly Dictionary<Guid, TimeSpan> _microStops = [];
    private readonly Dictionary<Guid, StopReasons> _stopReasons = [];
    private long _next = 1;

    /// <summary>Every stop written, by id, as last written.</summary>
    public Dictionary<Guid, MachineStop> Stops { get; } = [];

    public List<StateChangeRow> StateChanges { get; } = [];

    public List<MachineCountRow> Counts { get; } = [];

    public void AddSite(Guid siteId)
    {
        lock (_lock)
        {
            _sites.Add(siteId);
        }
    }

    public Guid AddEquipment(Guid siteId, string code, bool tracksState = true, Guid? profileId = null)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _equipment[id] = (new TagEquipment(id, siteId, code, tracksState), profileId);
        }

        return id;
    }

    /// <summary>A profile's state map: raw value to PackML state.</summary>
    public Guid AddProfile(Dictionary<int, int> map)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _maps[id] = map;
        }

        return id;
    }

    public Guid OpenRunOn(Guid equipmentId)
    {
        var run = new OpenRun(equipmentId, Guid.CreateVersion7(), Guid.CreateVersion7());
        lock (_lock)
        {
            _runs[equipmentId] = run;
        }

        return run.RunId;
    }

    public Versioned<EdgeNodeRecord> Node(Guid id)
    {
        lock (_lock)
        {
            return _nodes[id];
        }
    }

    public Versioned<EdgeConnectionRecord> Connection(Guid id)
    {
        lock (_lock)
        {
            return _connections[id];
        }
    }

    public Versioned<EquipmentTagRecord> Tag(Guid id)
    {
        lock (_lock)
        {
            return _tags[id];
        }
    }

    public bool HasTag(Guid id)
    {
        lock (_lock)
        {
            return _tags.ContainsKey(id);
        }
    }

    public MachineLive? Live(Guid equipmentId)
    {
        lock (_lock)
        {
            return _live.GetValueOrDefault(equipmentId);
        }
    }

    public IReadOnlyList<EdgeMetricReportDto> Metrics
    {
        get
        {
            lock (_lock)
            {
                return [.. _metrics.Values];
            }
        }
    }

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => Locked(() => _sites.Contains(siteId));

    public Task<bool> NodeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        Locked(() => _nodes.Values.Any(n => n.Value.SiteId == siteId && n.Value.Code == code));

    public Task<Versioned<EdgeNodeRecord>?> GetNodeForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _nodes.GetValueOrDefault(id));

    public Task<long> InsertNodeAsync(EdgeNodeRecord node, DateTimeOffset at, CancellationToken cancellationToken) => UpdateNodeAsync(node, at, cancellationToken);

    public Task<long> UpdateNodeAsync(EdgeNodeRecord node, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() =>
    {
        var version = _next++;
        _nodes[node.Id] = new(node, version);
        return version;
    });

    public Task<bool> ConnectionCodeExistsAsync(Guid edgeNodeId, string code, CancellationToken cancellationToken) =>
        Locked(() => _connections.Values.Any(c => c.Value.EdgeNodeId == edgeNodeId && c.Value.Code == code));

    public Task<Versioned<EdgeConnectionRecord>?> GetConnectionForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _connections.GetValueOrDefault(id));

    public Task<EdgeConnectionRecord?> GetConnectionAsync(Guid id, CancellationToken cancellationToken) =>
        Locked(() => _connections.TryGetValue(id, out var c) ? c.Value : null);

    public Task<IReadOnlyList<Versioned<EdgeConnectionRecord>>> ListConnectionsForUpdateAsync(Guid edgeNodeId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<Versioned<EdgeConnectionRecord>>>(() =>
            [.. _connections.Values.Where(c => c.Value.EdgeNodeId == edgeNodeId)]);

    public Task<long> InsertConnectionAsync(EdgeConnectionRecord connection, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpdateConnectionAsync(connection, at, cancellationToken);

    public Task<long> UpdateConnectionAsync(EdgeConnectionRecord connection, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() =>
    {
        var version = _next++;
        _connections[connection.Id] = new(connection, version);
        return version;
    });

    public Task<TagEquipment?> GetTagEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Locked(() => _equipment.TryGetValue(equipmentId, out var e) ? e.Equipment : null);

    public Task<Versioned<EquipmentTagRecord>?> GetTagForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _tags.GetValueOrDefault(id));

    public Task<EquipmentTagRecord?> GetTagAsync(Guid id, CancellationToken cancellationToken) =>
        Locked(() => _tags.TryGetValue(id, out var t) ? t.Value : null);

    public Task<IReadOnlyList<Versioned<EquipmentTagRecord>>> ListTagsForUpdateAsync(
        IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<Versioned<EquipmentTagRecord>>>(() =>
            [.. _tags.Values.Where(t => connectionIds.Contains(t.Value.ConnectionId))]);

    public Task<long> InsertTagAsync(EquipmentTagRecord tag, DateTimeOffset at, CancellationToken cancellationToken) => UpdateTagAsync(tag, at, cancellationToken);

    public Task<long> UpdateTagAsync(EquipmentTagRecord tag, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() =>
    {
        var version = _next++;
        _tags[tag.Id] = new(tag, version);
        return version;
    });

    public Task<IReadOnlyList<Guid>> DetachTagQualitySourcesAsync(Guid tagId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<int> DeleteTagAsync(Guid tagId, CancellationToken cancellationToken) =>
        Locked(() => _tags.Remove(tagId) ? 1 : 0);

    public Task<IReadOnlyList<IngestTag>> ListIngestTagsAsync(Guid edgeNodeId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<IngestTag>>(() =>
        [
            .. _tags.Values.Select(t => t.Value)
                .Where(t => tagIds.Contains(t.Id) && t.IsActive && _connections[t.ConnectionId].Value is { IsActive: true } c && c.EdgeNodeId == edgeNodeId)
                .Select(t => new IngestTag(t.Id, t.EquipmentId, t.Role, t.ScaleFactor, t.ScaleOffset, t.CounterRollover, _equipment[t.EquipmentId].ProfileId)),
        ]);

    public Task LockEquipmentAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<Guid, MachineLive>> GetLiveAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyDictionary<Guid, MachineLive>>(() => _live.Where(l => equipmentIds.Contains(l.Key)).ToDictionary());

    public Task<IReadOnlyDictionary<(Guid EquipmentId, string CountKind), long>> GetCountersAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyDictionary<(Guid EquipmentId, string CountKind), long>>(() => _counters.Where(c => equipmentIds.Contains(c.Key.Item1)).ToDictionary(c => c.Key, c => c.Value));

    public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, int>>> GetStateMapsAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, int>>>(() =>
            _maps.Where(m => profileIds.Contains(m.Key)).ToDictionary(m => m.Key, m => (IReadOnlyDictionary<int, int>)m.Value));

    public Task<IReadOnlyList<OpenRun>> GetOpenRunsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<OpenRun>>(() => [.. _runs.Values.Where(r => equipmentIds.Contains(r.EquipmentId))]);

    public Task UpsertLiveAsync(Guid equipmentId, MachineLive live, MachineStop? openStop, Guid edgeNodeId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            _openStops[equipmentId] = openStop?.Id;
            return _live[equipmentId] = live;
        });

    public Task<IReadOnlyDictionary<Guid, StopContext>> GetStopContextsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyDictionary<Guid, StopContext>>(() => equipmentIds.Where(_equipment.ContainsKey).ToDictionary(id => id, id => new StopContext(
            id,
            _microStops.GetValueOrDefault(id, MachineProfileRules.DefaultMicroStop),
            _stopReasons.GetValueOrDefault(id, StopReasons.None),
            _openStops.GetValueOrDefault(id) is { } stopId ? Stops[stopId] : null)));

    public Task InsertStopsAsync(IReadOnlyList<MachineStop> stops, CancellationToken cancellationToken) => Locked(() =>
    {
        foreach (var stop in stops)
        {
            Stops.Add(stop.Id, stop);
        }

        return 0;
    });

    public Task UpdateStopsAsync(IReadOnlyList<MachineStop> stops, CancellationToken cancellationToken) => Locked(() =>
    {
        foreach (var stop in stops)
        {
            if (!Stops.ContainsKey(stop.Id))
            {
                throw new InvalidOperationException($"Stop {stop.Id} was updated before it was written.");
            }

            Stops[stop.Id] = stop;
        }

        return 0;
    });

    /// <summary>A machine's micro-stop threshold and its profile's reasons, for its stops.</summary>
    public void SetStopRules(Guid equipmentId, TimeSpan microStop, StopReasons reasons)
    {
        lock (_lock)
        {
            _microStops[equipmentId] = microStop;
            _stopReasons[equipmentId] = reasons;
        }
    }

    public Task UpsertCounterAsync(Guid equipmentId, string countKind, long rawCounter, DateTimeOffset readAt, CancellationToken cancellationToken) =>
        Locked(() => _counters[(equipmentId, countKind)] = rawCounter);

    public Task InsertStateChangesAsync(IReadOnlyList<StateChangeRow> rows, CancellationToken cancellationToken) => Locked(() =>
    {
        StateChanges.AddRange(rows);
        return 0;
    });

    public Task InsertCountsAsync(IReadOnlyList<MachineCountRow> rows, string actorId, CancellationToken cancellationToken) => Locked(() =>
    {
        Counts.AddRange(rows);
        return 0;
    });

    public Task<IReadOnlyList<Guid>> ListNodeConnectionsAsync(Guid edgeNodeId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<Guid>>(() => [.. _connections.Values.Where(c => c.Value.EdgeNodeId == edgeNodeId && connectionIds.Contains(c.Value.Id)).Select(c => c.Value.Id)]);

    public Task UpsertMetricsAsync(IReadOnlyList<EdgeMetricReportDto> metrics, CancellationToken cancellationToken) => Locked(() =>
    {
        foreach (var m in metrics)
        {
            _metrics[(m.ConnectionId, m.Address)] = m;
        }

        return 0;
    });

    public Task<IReadOnlyList<EdgeNodeDto>> ListNodesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<EdgeNodeDto>>(() => [.. _nodes.Values.Where(n => n.Value.SiteId == siteId).Select(ToDto)]);

    public Task<EdgeNodeDetailDto?> GetNodeAsync(Guid id, CancellationToken cancellationToken) =>
        Locked(() => _nodes.TryGetValue(id, out var n) ? new EdgeNodeDetailDto { Node = ToDto(n) } : null);

    public Task<EdgeConfigDto?> GetConfigAsync(Guid id, CancellationToken cancellationToken) => Locked(() =>
        _nodes.TryGetValue(id, out var n)
            ? new EdgeConfigDto
            {
                EdgeNodeId = id,
                Code = n.Value.Code,
                SiteId = n.Value.SiteId,
                IsActive = n.Value.IsActive,
                Tags = [.. _tags.Values.Select(t => t.Value).Where(t => t.IsActive && _connections[t.ConnectionId].Value.EdgeNodeId == id)
                    .Select(t => new EdgeTagConfigDto { Id = t.Id, EquipmentId = t.EquipmentId, Role = t.Role, ConnectionId = t.ConnectionId, Address = t.Address })],
                StreamId = n.Value.StreamId,
                LastSequence = n.Value.LastSequence,
            }
            : null);

    private static EdgeNodeDto ToDto(Versioned<EdgeNodeRecord> n) => new()
    {
        Id = n.Value.Id,
        SiteId = n.Value.SiteId,
        Code = n.Value.Code,
        Name = n.Value.Name,
        IsActive = n.Value.IsActive,
        LastSeenAt = n.Value.LastSeenAt,
        LastSequence = n.Value.LastSequence,
        Version = n.Version,
    };

    private Task<T> Locked<T>(Func<T> read)
    {
        lock (_lock)
        {
            return Task.FromResult(read());
        }
    }
}
