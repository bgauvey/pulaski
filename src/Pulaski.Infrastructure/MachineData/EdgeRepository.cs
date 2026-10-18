using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Write side for edge nodes, connections, tags and machine data ingest (D-041). Every statement runs in the command's transaction.</summary>
internal sealed class EdgeRepository(DbSession session) : IEdgeRepository
{
    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')";

    private const string NodeExists = "SELECT EXISTS (SELECT 1 FROM core.edge_node WHERE site_id = @SiteId AND code = @Code)";

    private const string GetNode = """
        SELECT id, site_id, code, name, description, is_active, stream_id, last_sequence, last_seen_at, agent_version, xmin::text::bigint AS version
        FROM core.edge_node WHERE id = @Id FOR UPDATE
        """;

    private const string InsertNode = """
        INSERT INTO core.edge_node (id, site_id, code, name, description, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @Description, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateNode = """
        UPDATE core.edge_node
        SET name = @Name, description = @Description, is_active = @IsActive, stream_id = @StreamId, last_sequence = @LastSequence,
            last_seen_at = @LastSeenAt, agent_version = @AgentVersion, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string ConnectionExists = "SELECT EXISTS (SELECT 1 FROM core.edge_connection WHERE edge_node_id = @NodeId AND code = @Code)";

    private const string GetConnection = """
        SELECT id, edge_node_id, code, name, protocol, settings::text AS settings_json, is_active, xmin::text::bigint AS version
        FROM core.edge_connection WHERE id = @Id FOR UPDATE
        """;

    private const string InsertConnection = """
        INSERT INTO core.edge_connection (id, edge_node_id, code, name, protocol, settings, is_active, created_at, updated_at)
        VALUES (@Id, @EdgeNodeId, @Code, @Name, @Protocol, @Settings::jsonb, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateConnection = """
        UPDATE core.edge_connection SET name = @Name, settings = @Settings::jsonb, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string TagEquipment = """
        SELECT id, CASE WHEN level = 'site' THEN id ELSE site_id END AS site_id, code, tracks_state FROM core.equipment WHERE id = @Id
        """;

    private const string GetTag = """
        SELECT id, equipment_id, tag_role AS role, connection_id, source_address AS address, scale_factor, scale_offset, counter_rollover, is_active,
               xmin::text::bigint AS version
        FROM core.equipment_tag WHERE id = @Id FOR UPDATE
        """;

    private const string InsertTag = """
        INSERT INTO core.equipment_tag (id, equipment_id, tag_role, connection_id, source_address, scale_factor, scale_offset, counter_rollover, is_active, created_at, updated_at)
        VALUES (@Id, @EquipmentId, @Role, @ConnectionId, @Address, @ScaleFactor, @ScaleOffset, @CounterRollover, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateTag = """
        UPDATE core.equipment_tag
        SET source_address = @Address, scale_factor = @ScaleFactor, scale_offset = @ScaleOffset, counter_rollover = @CounterRollover, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string IngestTags = """
        SELECT t.id AS tag_id, t.equipment_id, t.tag_role AS role, t.scale_factor, t.scale_offset, t.counter_rollover, e.machine_profile_id
        FROM core.equipment_tag t
        JOIN core.edge_connection c ON c.id = t.connection_id
        JOIN core.equipment e ON e.id = t.equipment_id
        WHERE c.edge_node_id = @NodeId AND t.id = ANY(@Ids) AND t.is_active AND c.is_active
        """;

    private const string LockEquipment = "SELECT id FROM core.equipment WHERE id = ANY(@Ids) ORDER BY id FOR NO KEY UPDATE";

    private const string Live = """
        SELECT equipment_id, raw_state, packml_state::int AS packml_state, unit_mode::int AS unit_mode, state_since, stop_reason, machine_speed, signal_at
        FROM core.equipment_live_state WHERE equipment_id = ANY(@Ids)
        """;

    private const string CounterValues = "SELECT equipment_id, count_kind, raw_counter FROM core.equipment_counter WHERE equipment_id = ANY(@Ids)";

    private const string StateMaps = """
        SELECT profile_id, raw_value, packml_state::int AS packml_state FROM core.machine_profile_state WHERE profile_id = ANY(@Ids) AND is_active
        """;

    private const string OpenRuns = "SELECT equipment_id, id AS run_id, job_id FROM core.job_run WHERE equipment_id = ANY(@Ids) AND ended_at IS NULL";

    private const string UpsertLive = """
        INSERT INTO core.equipment_live_state (equipment_id, raw_state, packml_state, unit_mode, state_since, stop_reason, machine_speed, signal_at, edge_node_id, updated_at)
        VALUES (@EquipmentId, @RawState, @PackMlState, @UnitMode, @StateSince, @StopReason, @MachineSpeed, @SignalAt, @EdgeNodeId, @At)
        ON CONFLICT (equipment_id) DO UPDATE
        SET raw_state = excluded.raw_state, packml_state = excluded.packml_state, unit_mode = excluded.unit_mode, state_since = excluded.state_since,
            stop_reason = excluded.stop_reason, machine_speed = excluded.machine_speed, signal_at = excluded.signal_at,
            edge_node_id = excluded.edge_node_id, updated_at = excluded.updated_at
        """;

    private const string UpsertCounter = """
        INSERT INTO core.equipment_counter (equipment_id, count_kind, raw_counter, read_at) VALUES (@EquipmentId, @CountKind, @RawCounter, @ReadAt)
        ON CONFLICT (equipment_id, count_kind) DO UPDATE SET raw_counter = excluded.raw_counter, read_at = excluded.read_at
        """;

    private const string InsertStateChange = """
        INSERT INTO ts.equipment_state_change (id, occurred_at, equipment_id, packml_state, unit_mode, prev_packml_state, prev_unit_mode, prev_started_at,
                                               run_id, source, edge_sequence, raw_state, stop_reason)
        VALUES (@Id, @OccurredAt, @EquipmentId, @PackMlState, @UnitMode, @PrevPackMlState, @PrevUnitMode, @PrevStartedAt,
                @RunId, @Source, @EdgeSequence, @RawState, @StopReason)
        """;

    private const string InsertCount = """
        INSERT INTO ts.production_count (id, occurred_at, equipment_id, run_id, job_id, count_kind, delta_qty, raw_counter, source, actor_id)
        VALUES (@Id, @OccurredAt, @EquipmentId, @RunId, @JobId, @CountKind, @Quantity, @RawCounter, 'machine', @ActorId)
        """;

    private const string NodeConnections = "SELECT id FROM core.edge_connection WHERE edge_node_id = @NodeId AND id = ANY(@Ids)";

    private const string UpsertMetric = """
        INSERT INTO core.edge_metric (connection_id, address, data_type, last_value, last_seen_at)
        VALUES (@ConnectionId, @Address, @DataType, @Value, @SeenAt)
        ON CONFLICT (connection_id, address) DO UPDATE
        SET data_type = coalesce(excluded.data_type, core.edge_metric.data_type), last_value = excluded.last_value,
            last_seen_at = greatest(excluded.last_seen_at, core.edge_metric.last_seen_at)
        """;

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => session.ExecuteScalarAsync<bool>(IsSite, new { Id = siteId }, cancellationToken);

    public Task<bool> NodeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(NodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public async Task<Versioned<EdgeNodeRecord>?> GetNodeForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<NodeRow>(GetNode, new { Id = id }, cancellationToken) is { } r
            ? new(new EdgeNodeRecord(r.Id, r.SiteId, r.Code, r.Name, r.Description, r.IsActive, r.StreamId, r.LastSequence, r.LastSeenAt, r.AgentVersion), r.Version)
            : null;

    public Task<long> InsertNodeAsync(EdgeNodeRecord n, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertNode, new { n.Id, n.SiteId, n.Code, n.Name, n.Description, n.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateNodeAsync(EdgeNodeRecord n, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateNode, new { n.Id, n.Name, n.Description, n.IsActive, n.StreamId, n.LastSequence, n.LastSeenAt, n.AgentVersion, At = at }, cancellationToken);

    public Task<bool> ConnectionCodeExistsAsync(Guid edgeNodeId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(ConnectionExists, new { NodeId = edgeNodeId, Code = code }, cancellationToken);

    public async Task<Versioned<EdgeConnectionRecord>?> GetConnectionForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ConnectionRow>(GetConnection, new { Id = id }, cancellationToken) is { } r
            ? new(new EdgeConnectionRecord(r.Id, r.EdgeNodeId, r.Code, r.Name, r.Protocol, EdgeSql.ReadSettings(r.SettingsJson), r.IsActive), r.Version)
            : null;

    public Task<long> InsertConnectionAsync(EdgeConnectionRecord c, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertConnection,
            new { c.Id, c.EdgeNodeId, c.Code, c.Name, c.Protocol, Settings = JsonSerializer.Serialize(c.Settings), c.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateConnectionAsync(EdgeConnectionRecord c, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateConnection, new { c.Id, c.Name, Settings = JsonSerializer.Serialize(c.Settings), c.IsActive, At = at }, cancellationToken);

    public async Task<TagEquipment?> GetTagEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<EquipmentRow>(TagEquipment, new { Id = equipmentId }, cancellationToken) is { } r
            ? new TagEquipment(r.Id, r.SiteId, r.Code, r.TracksState)
            : null;

    public async Task<Versioned<EquipmentTagRecord>?> GetTagForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<TagRow>(GetTag, new { Id = id }, cancellationToken) is { } r
            ? new(new EquipmentTagRecord(r.Id, r.EquipmentId, r.Role, r.ConnectionId, r.Address, r.ScaleFactor, r.ScaleOffset, r.CounterRollover, r.IsActive), r.Version)
            : null;

    public Task<long> InsertTagAsync(EquipmentTagRecord t, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertTag,
            new { t.Id, t.EquipmentId, t.Role, t.ConnectionId, t.Address, t.ScaleFactor, t.ScaleOffset, t.CounterRollover, t.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateTagAsync(EquipmentTagRecord t, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateTag, new { t.Id, t.Address, t.ScaleFactor, t.ScaleOffset, t.CounterRollover, t.IsActive, At = at }, cancellationToken);

    public async Task<IReadOnlyList<IngestTag>> ListIngestTagsAsync(Guid edgeNodeId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<IngestTagRow>(IngestTags, new { NodeId = edgeNodeId, Ids = tagIds.ToArray() }, cancellationToken))
            .Select(r => new IngestTag(r.TagId, r.EquipmentId, r.Role, r.ScaleFactor, r.ScaleOffset, r.CounterRollover, r.MachineProfileId))];

    public Task LockEquipmentAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(LockEquipment, new { Ids = equipmentIds.ToArray() }, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, MachineLive>> GetLiveAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        (await session.QueryAsync<LiveRow>(Live, new { Ids = equipmentIds.ToArray() }, cancellationToken))
            .ToDictionary(r => r.EquipmentId, r => new MachineLive(r.RawState, r.PackmlState, r.UnitMode, r.StateSince, r.StopReason, r.MachineSpeed, r.SignalAt));

    public async Task<IReadOnlyDictionary<(Guid EquipmentId, string CountKind), long>> GetCountersAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        (await session.QueryAsync<CounterRow>(CounterValues, new { Ids = equipmentIds.ToArray() }, cancellationToken))
            .ToDictionary(r => (r.EquipmentId, r.CountKind), r => r.RawCounter);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, int>>> GetStateMapsAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken) =>
        (await session.QueryAsync<StateMapRow>(StateMaps, new { Ids = profileIds.ToArray() }, cancellationToken))
            .GroupBy(r => r.ProfileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<int, int>)g.ToDictionary(r => r.RawValue, r => r.PackmlState));

    public async Task<IReadOnlyList<OpenRun>> GetOpenRunsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<RunRow>(OpenRuns, new { Ids = equipmentIds.ToArray() }, cancellationToken)).Select(r => new OpenRun(r.EquipmentId, r.RunId, r.JobId))];

    public Task UpsertLiveAsync(Guid equipmentId, MachineLive live, Guid edgeNodeId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpsertLive, new
        {
            EquipmentId = equipmentId,
            live.RawState,
            PackMlState = (short)live.PackMlState,
            UnitMode = (short)live.UnitMode,
            live.StateSince,
            live.StopReason,
            live.MachineSpeed,
            live.SignalAt,
            EdgeNodeId = edgeNodeId,
            At = at,
        }, cancellationToken);

    public Task UpsertCounterAsync(Guid equipmentId, string countKind, long rawCounter, DateTimeOffset readAt, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpsertCounter, new { EquipmentId = equipmentId, CountKind = countKind, RawCounter = rawCounter, ReadAt = readAt }, cancellationToken);

    public async Task InsertStateChangesAsync(IReadOnlyList<StateChangeRow> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            var c = row.Change;
            await session.ExecuteAsync(InsertStateChange, new
            {
                row.Id,
                c.OccurredAt,
                row.EquipmentId,
                PackMlState = (short)c.PackMlState,
                UnitMode = (short)c.UnitMode,
                PrevPackMlState = (short?)c.PrevPackMlState,
                PrevUnitMode = (short?)c.PrevUnitMode,
                c.PrevStartedAt,
                row.RunId,
                Source = c.SignalLost ? "edge_gap" : "packtags",
                row.EdgeSequence,
                c.RawState,
                c.StopReason,
            }, cancellationToken);
        }
    }

    public async Task InsertCountsAsync(IReadOnlyList<MachineCountRow> rows, string actorId, CancellationToken cancellationToken)
    {
        foreach (var r in rows)
        {
            await session.ExecuteAsync(InsertCount, new { r.Id, r.OccurredAt, r.EquipmentId, r.RunId, r.JobId, r.CountKind, r.Quantity, r.RawCounter, ActorId = actorId }, cancellationToken);
        }
    }

    public Task<IReadOnlyList<Guid>> ListNodeConnectionsAsync(Guid edgeNodeId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(NodeConnections, new { NodeId = edgeNodeId, Ids = connectionIds.ToArray() }, cancellationToken);

    public async Task UpsertMetricsAsync(IReadOnlyList<EdgeMetricReportDto> metrics, CancellationToken cancellationToken)
    {
        foreach (var m in metrics)
        {
            await session.ExecuteAsync(UpsertMetric, m, cancellationToken);
        }
    }

    private sealed class NodeRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public bool IsActive { get; init; }
        public Guid? StreamId { get; init; }
        public long LastSequence { get; init; }
        public DateTimeOffset? LastSeenAt { get; init; }
        public string? AgentVersion { get; init; }
        public long Version { get; init; }
    }

    private sealed class ConnectionRow
    {
        public Guid Id { get; init; }
        public Guid EdgeNodeId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Protocol { get; init; } = "";
        public string SettingsJson { get; init; } = "{}";
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class EquipmentRow
    {
        public Guid Id { get; init; }
        public Guid? SiteId { get; init; }
        public string Code { get; init; } = "";
        public bool TracksState { get; init; }
    }

    private sealed class TagRow
    {
        public Guid Id { get; init; }
        public Guid EquipmentId { get; init; }
        public string Role { get; init; } = "";
        public Guid ConnectionId { get; init; }
        public string Address { get; init; } = "";
        public decimal ScaleFactor { get; init; }
        public decimal ScaleOffset { get; init; }
        public long? CounterRollover { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class IngestTagRow
    {
        public Guid TagId { get; init; }
        public Guid EquipmentId { get; init; }
        public string Role { get; init; } = "";
        public decimal ScaleFactor { get; init; }
        public decimal ScaleOffset { get; init; }
        public long? CounterRollover { get; init; }
        public Guid? MachineProfileId { get; init; }
    }

    private sealed class LiveRow
    {
        public Guid EquipmentId { get; init; }
        public int? RawState { get; init; }
        public int PackmlState { get; init; }
        public int UnitMode { get; init; }
        public DateTimeOffset StateSince { get; init; }
        public int? StopReason { get; init; }
        public decimal? MachineSpeed { get; init; }
        public DateTimeOffset SignalAt { get; init; }
    }

    private sealed class CounterRow
    {
        public Guid EquipmentId { get; init; }
        public string CountKind { get; init; } = "";
        public long RawCounter { get; init; }
    }

    private sealed class StateMapRow
    {
        public Guid ProfileId { get; init; }
        public int RawValue { get; init; }
        public int PackmlState { get; init; }
    }

    private sealed class RunRow
    {
        public Guid EquipmentId { get; init; }
        public Guid RunId { get; init; }
        public Guid JobId { get; init; }
    }
}

internal static class EdgeSql
{
    public static IReadOnlyDictionary<string, string> ReadSettings(string? json) =>
        string.IsNullOrEmpty(json) ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}
