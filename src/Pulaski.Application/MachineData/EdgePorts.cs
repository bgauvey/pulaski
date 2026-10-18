using Pulaski.Application.Abstractions;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

public sealed record EdgeNodeRecord(
    Guid Id, Guid SiteId, string Code, string Name, string? Description, bool IsActive, Guid? StreamId, long LastSequence, DateTimeOffset? LastSeenAt,
    string? AgentVersion);

public sealed record EdgeConnectionRecord(Guid Id, Guid EdgeNodeId, string Code, string Name, string Protocol, IReadOnlyDictionary<string, string> Settings, bool IsActive)
{
    /// <summary>Equal fields, the settings compared by content.</summary>
    public bool SameAs(EdgeConnectionRecord other) =>
        this with { Settings = other.Settings } == other
        && Settings.Count == other.Settings.Count
        && Settings.All(s => other.Settings.TryGetValue(s.Key, out var v) && v == s.Value);
}

public sealed record EquipmentTagRecord(
    Guid Id, Guid EquipmentId, string Role, Guid ConnectionId, string Address, decimal ScaleFactor, decimal ScaleOffset, long? CounterRollover, bool IsActive);

/// <summary>Equipment as a tag binding checks it.</summary>
public sealed record TagEquipment(Guid Id, Guid? SiteId, string Code, bool TracksState);

/// <summary>An active tag of an edge node, with what applying its signals needs.</summary>
public sealed record IngestTag(Guid TagId, Guid EquipmentId, string Role, decimal ScaleFactor, decimal ScaleOffset, long? CounterRollover, Guid? MachineProfileId);

/// <summary>The run open on a piece of equipment, which machine counts go into (D-035).</summary>
public sealed record OpenRun(Guid EquipmentId, Guid RunId, Guid JobId);

/// <summary>A row for <c>ts.equipment_state_change</c>.</summary>
public sealed record StateChangeRow(Guid Id, Guid EquipmentId, Guid? RunId, long EdgeSequence, StateChange Change);

/// <summary>A machine count for <c>ts.production_count</c> (source <c>machine</c>).</summary>
public sealed record MachineCountRow(Guid Id, DateTimeOffset OccurredAt, Guid EquipmentId, Guid? RunId, Guid? JobId, string CountKind, decimal Quantity, long RawCounter);

/// <summary>Write side for edge nodes, their connections and tags, and machine data ingest (D-041).</summary>
public interface IEdgeRepository
{
    Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<bool> NodeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<Versioned<EdgeNodeRecord>?> GetNodeForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertNodeAsync(EdgeNodeRecord node, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateNodeAsync(EdgeNodeRecord node, DateTimeOffset at, CancellationToken cancellationToken);

    Task<bool> ConnectionCodeExistsAsync(Guid edgeNodeId, string code, CancellationToken cancellationToken);

    Task<Versioned<EdgeConnectionRecord>?> GetConnectionForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertConnectionAsync(EdgeConnectionRecord connection, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateConnectionAsync(EdgeConnectionRecord connection, DateTimeOffset at, CancellationToken cancellationToken);

    Task<TagEquipment?> GetTagEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task<Versioned<EquipmentTagRecord>?> GetTagForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertTagAsync(EquipmentTagRecord tag, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateTagAsync(EquipmentTagRecord tag, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The node's active tags among <paramref name="tagIds"/>, read through its active connections.</summary>
    Task<IReadOnlyList<IngestTag>> ListIngestTagsAsync(Guid edgeNodeId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <summary>Locks the equipment, in id order (ordered in SQL), so two batches touching the same machines serialize.</summary>
    Task LockEquipmentAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, MachineLive>> GetLiveAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<(Guid EquipmentId, string CountKind), long>> GetCountersAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);

    /// <summary>Each profile's active state values and the PackML state they mean.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, int>>> GetStateMapsAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<OpenRun>> GetOpenRunsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);

    Task UpsertLiveAsync(Guid equipmentId, MachineLive live, Guid edgeNodeId, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpsertCounterAsync(Guid equipmentId, string countKind, long rawCounter, DateTimeOffset readAt, CancellationToken cancellationToken);

    Task InsertStateChangesAsync(IReadOnlyList<StateChangeRow> rows, CancellationToken cancellationToken);

    Task InsertCountsAsync(IReadOnlyList<MachineCountRow> rows, string actorId, CancellationToken cancellationToken);

    /// <summary>The connections among <paramref name="connectionIds"/> that belong to the node.</summary>
    Task<IReadOnlyList<Guid>> ListNodeConnectionsAsync(Guid edgeNodeId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken);

    Task UpsertMetricsAsync(IReadOnlyList<EdgeMetricReportDto> metrics, CancellationToken cancellationToken);
}

/// <summary>Read side for edge nodes (D-041).</summary>
public interface IEdgeReadStore
{
    Task<IReadOnlyList<EdgeNodeDto>> ListNodesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<EdgeNodeDetailDto?> GetNodeAsync(Guid id, CancellationToken cancellationToken);

    Task<EdgeConfigDto?> GetConfigAsync(Guid id, CancellationToken cancellationToken);
}
