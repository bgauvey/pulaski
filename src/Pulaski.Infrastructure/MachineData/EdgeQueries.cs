using Dapper;
using Npgsql;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Read side for edge nodes (D-041).</summary>
internal sealed class EdgeQueries(NpgsqlDataSource dataSource) : IEdgeReadStore
{
    private const string Nodes = """
        SELECT n.id, n.site_id, n.code, n.name, n.description, n.is_active, n.last_seen_at, n.agent_version, n.last_sequence,
               (SELECT count(*)::int FROM core.edge_connection c WHERE c.edge_node_id = n.id AND c.is_active) AS connection_count,
               (SELECT count(*)::int FROM core.equipment_tag t JOIN core.edge_connection c ON c.id = t.connection_id
                WHERE c.edge_node_id = n.id AND t.is_active) AS tag_count,
               n.xmin::text::bigint AS version
        FROM core.edge_node n
        """;

    private const string ListSql = $"{Nodes} WHERE n.site_id = @SiteId ORDER BY n.code";

    private const string GetSql = $"{Nodes} WHERE n.id = @Id";

    private const string ConnectionsSql = """
        SELECT id, edge_node_id, code, name, protocol, settings::text AS settings_json, is_active, xmin::text::bigint AS version
        FROM core.edge_connection WHERE edge_node_id = @Id ORDER BY code
        """;

    private const string TagsSql = """
        SELECT t.id, t.equipment_id, e.code AS equipment_code, t.tag_role AS role, t.connection_id, c.code AS connection_code, t.source_address AS address,
               t.scale_factor, t.scale_offset, t.counter_rollover, t.is_active, m.last_value, m.last_seen_at, t.xmin::text::bigint AS version
        FROM core.equipment_tag t
        JOIN core.edge_connection c ON c.id = t.connection_id
        JOIN core.equipment e ON e.id = t.equipment_id
        LEFT JOIN core.edge_metric m ON m.connection_id = t.connection_id AND m.address = t.source_address
        WHERE c.edge_node_id = @Id
        ORDER BY e.code, t.tag_role
        """;

    private const string MetricsSql = """
        SELECT m.connection_id, m.address, m.data_type, m.last_value, m.last_seen_at, t.id AS tag_id
        FROM core.edge_metric m
        JOIN core.edge_connection c ON c.id = m.connection_id
        LEFT JOIN core.equipment_tag t ON t.connection_id = m.connection_id AND t.source_address = m.address AND t.is_active
        WHERE c.edge_node_id = @Id
        ORDER BY m.address
        """;

    private const string LiveSql = """
        SELECT l.equipment_id, e.code AS equipment_code, l.raw_state, l.packml_state::int AS pack_ml_state, l.unit_mode::int AS unit_mode, l.state_since,
               l.stop_reason, pc.message AS stop_reason_text, l.machine_speed, l.signal_at
        FROM core.equipment_live_state l
        JOIN core.equipment e ON e.id = l.equipment_id
        LEFT JOIN core.machine_profile_code pc ON pc.profile_id = e.machine_profile_id AND pc.code_kind = 'stop_reason' AND pc.raw_value = l.stop_reason
        WHERE l.equipment_id IN (SELECT t.equipment_id FROM core.equipment_tag t JOIN core.edge_connection c ON c.id = t.connection_id
                                 WHERE c.edge_node_id = @Id AND t.is_active)
        ORDER BY e.code
        """;

    private const string ConfigNodeSql = "SELECT id, code, site_id, is_active, stream_id, last_sequence FROM core.edge_node WHERE id = @Id";

    private const string ConfigTagsSql = """
        SELECT t.id, t.equipment_id, t.tag_role AS role, t.connection_id, t.source_address AS address
        FROM core.equipment_tag t
        JOIN core.edge_connection c ON c.id = t.connection_id
        WHERE c.edge_node_id = @Id AND c.is_active AND t.is_active
        ORDER BY t.id
        """;

    public async Task<IReadOnlyList<EdgeNodeDto>> ListNodesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<EdgeNodeDto>(new CommandDefinition(ListSql, new { SiteId = siteId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<EdgeNodeDetailDto?> GetNodeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = id };
        var node = await connection.QuerySingleOrDefaultAsync<EdgeNodeDto>(new CommandDefinition(GetSql, args, cancellationToken: cancellationToken));
        if (node is null)
        {
            return null;
        }

        var connections = await ConnectionsAsync(connection, id, cancellationToken);
        var tags = await connection.QueryAsync<EquipmentTagDto>(new CommandDefinition(TagsSql, args, cancellationToken: cancellationToken));
        var metrics = await connection.QueryAsync<EdgeMetricDto>(new CommandDefinition(MetricsSql, args, cancellationToken: cancellationToken));
        var live = await connection.QueryAsync<LiveRow>(new CommandDefinition(LiveSql, args, cancellationToken: cancellationToken));
        return new EdgeNodeDetailDto
        {
            Node = node,
            Connections = connections,
            Tags = tags.AsList(),
            Metrics = metrics.AsList(),
            Live = [.. live.Select(l => new EquipmentLiveDto
            {
                EquipmentId = l.EquipmentId,
                EquipmentCode = l.EquipmentCode,
                RawState = l.RawState,
                PackMlState = l.PackMlState,
                PackMlName = PackMlStates.Name(l.PackMlState),
                UnitMode = l.UnitMode,
                StateSince = l.StateSince,
                StopReason = l.StopReason,
                StopReasonText = l.StopReasonText,
                MachineSpeed = l.MachineSpeed,
                SignalAt = l.SignalAt,
            })],
        };
    }

    public async Task<EdgeConfigDto?> GetConfigAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var node = await connection.QuerySingleOrDefaultAsync<ConfigNodeRow>(new CommandDefinition(ConfigNodeSql, new { Id = id }, cancellationToken: cancellationToken));
        if (node is null)
        {
            return null;
        }

        var connections = (await ConnectionsAsync(connection, id, cancellationToken)).Where(c => c.IsActive).ToList();
        var tags = await connection.QueryAsync<EdgeTagConfigDto>(new CommandDefinition(ConfigTagsSql, new { Id = id }, cancellationToken: cancellationToken));
        return new EdgeConfigDto
        {
            EdgeNodeId = node.Id,
            Code = node.Code,
            SiteId = node.SiteId,
            IsActive = node.IsActive,
            Connections = connections,
            Tags = tags.AsList(),
            StreamId = node.StreamId,
            LastSequence = node.LastSequence,
        };
    }

    private static async Task<List<EdgeConnectionDto>> ConnectionsAsync(NpgsqlConnection connection, Guid nodeId, CancellationToken cancellationToken) =>
        [.. (await connection.QueryAsync<ConnectionRow>(new CommandDefinition(ConnectionsSql, new { Id = nodeId }, cancellationToken: cancellationToken)))
            .Select(r => new EdgeConnectionDto
            {
                Id = r.Id,
                EdgeNodeId = r.EdgeNodeId,
                Code = r.Code,
                Name = r.Name,
                Protocol = r.Protocol,
                Settings = EdgeSql.ReadSettings(r.SettingsJson),
                IsActive = r.IsActive,
                Version = r.Version,
            })];

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

    private sealed class LiveRow
    {
        public Guid EquipmentId { get; init; }
        public string EquipmentCode { get; init; } = "";
        public int? RawState { get; init; }
        public int PackMlState { get; init; }
        public int UnitMode { get; init; }
        public DateTimeOffset StateSince { get; init; }
        public int? StopReason { get; init; }
        public string? StopReasonText { get; init; }
        public decimal? MachineSpeed { get; init; }
        public DateTimeOffset SignalAt { get; init; }
    }

    private sealed class ConfigNodeRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid SiteId { get; init; }
        public bool IsActive { get; init; }
        public Guid? StreamId { get; init; }
        public long LastSequence { get; init; }
    }
}
