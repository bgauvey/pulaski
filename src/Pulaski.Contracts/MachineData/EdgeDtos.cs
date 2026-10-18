namespace Pulaski.Contracts.MachineData;

// ---- Maintained on the Edge nodes screen (D-041) ----

/// <summary>A Pulaski Edge node: a worker on plant hardware that runs connectors and forwards what they read.</summary>
public sealed record EdgeNodeDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; }

    /// <summary>When it last forwarded anything, and what it runs.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }
    public string? AgentVersion { get; init; }
    public long LastSequence { get; init; }
    public int ConnectionCount { get; init; }
    public int TagCount { get; init; }
    public long Version { get; init; }
}

/// <summary>A data source an edge node reads through one connector, such as a Sparkplug B broker.</summary>
public sealed record EdgeConnectionDto
{
    public required Guid Id { get; init; }
    public required Guid EdgeNodeId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary><c>sparkplug_b</c>.</summary>
    public required string Protocol { get; init; }

    /// <summary>The connector's settings, such as <c>host</c>, <c>port</c>, <c>tls</c>, <c>groupId</c>; never secrets.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

/// <summary>A signal bound to a piece of equipment: which role it plays and where the edge reads it.</summary>
public sealed record EquipmentTagDto
{
    public required Guid Id { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }

    /// <summary><c>state</c>, <c>unit_mode</c>, <c>stop_reason</c>, <c>processed_count</c>, <c>defective_count</c>, <c>consumed_count</c> or <c>machine_speed</c>.</summary>
    public required string Role { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string ConnectionCode { get; init; }

    /// <summary>For Sparkplug B: <c>group/node[/device]:metric</c>.</summary>
    public required string Address { get; init; }
    public decimal ScaleFactor { get; init; } = 1;
    public decimal ScaleOffset { get; init; }
    public long? CounterRollover { get; init; }
    public bool IsActive { get; init; }

    /// <summary>The value the edge last reported for the address, and when.</summary>
    public string? LastValue { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public long Version { get; init; }
}

/// <summary>An address a connection has seen, and the tag bound to it, if any.</summary>
public sealed record EdgeMetricDto
{
    public required Guid ConnectionId { get; init; }
    public required string Address { get; init; }
    public string? DataType { get; init; }
    public string? LastValue { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public Guid? TagId { get; init; }
}

/// <summary>A machine's state as its signals last left it (D-041).</summary>
public sealed record EquipmentLiveDto
{
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public int? RawState { get; init; }
    public int PackMlState { get; init; }
    public required string PackMlName { get; init; }
    public int UnitMode { get; init; }
    public DateTimeOffset StateSince { get; init; }
    public int? StopReason { get; init; }

    /// <summary>The stop code's message from the machine's profile.</summary>
    public string? StopReasonText { get; init; }
    public decimal? MachineSpeed { get; init; }
    public DateTimeOffset SignalAt { get; init; }
}

public sealed record EdgeNodeDetailDto
{
    public required EdgeNodeDto Node { get; init; }
    public IReadOnlyList<EdgeConnectionDto> Connections { get; init; } = [];
    public IReadOnlyList<EquipmentTagDto> Tags { get; init; } = [];
    public IReadOnlyList<EdgeMetricDto> Metrics { get; init; } = [];
    public IReadOnlyList<EquipmentLiveDto> Live { get; init; } = [];
}

public sealed record CreateEdgeNodeRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
}

public sealed record UpdateEdgeNodeRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record CreateEdgeConnectionRequest
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "sparkplug_b";
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

public sealed record UpdateEdgeConnectionRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
    public bool IsActive { get; init; } = true;
}

public sealed record CreateEquipmentTagRequest
{
    public Guid EquipmentId { get; init; }
    public string Role { get; init; } = "";
    public Guid ConnectionId { get; init; }
    public string Address { get; init; } = "";
    public decimal ScaleFactor { get; init; } = 1;
    public decimal ScaleOffset { get; init; }
    public long? CounterRollover { get; init; }
}

public sealed record UpdateEquipmentTagRequest
{
    public long Version { get; init; }
    public string Address { get; init; } = "";
    public decimal ScaleFactor { get; init; } = 1;
    public decimal ScaleOffset { get; init; }
    public long? CounterRollover { get; init; }
    public bool IsActive { get; init; } = true;
}

// ---- Used by the edge itself (permission edge.ingest) ----

/// <summary>What an edge node reads (D-041): its connections and the tags bound through them.</summary>
public sealed record EdgeConfigDto
{
    public required Guid EdgeNodeId { get; init; }
    public required string Code { get; init; }
    public required Guid SiteId { get; init; }
    public bool IsActive { get; init; }
    public IReadOnlyList<EdgeConnectionDto> Connections { get; init; } = [];
    public IReadOnlyList<EdgeTagConfigDto> Tags { get; init; } = [];

    /// <summary>What the API has applied of the edge's buffer, so a restarted edge knows where it stands.</summary>
    public Guid? StreamId { get; init; }
    public long LastSequence { get; init; }
}

public sealed record EdgeTagConfigDto
{
    public required Guid Id { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string Role { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string Address { get; init; }
}

/// <summary>A value the edge read for a tag. Values are numbers: booleans as 0 and 1. <see cref="Good"/> false means no signal.</summary>
public sealed record EdgeSignalDto
{
    /// <summary>Its place in the edge's buffer: increasing, one stream after another.</summary>
    public long Sequence { get; init; }
    public Guid TagId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public decimal? Value { get; init; }
    public bool Good { get; init; } = true;
}

public sealed record EdgeSignalBatchRequest
{
    /// <summary>The edge's buffer: a new one (the buffer was lost or reset) starts its sequence again.</summary>
    public Guid StreamId { get; init; }
    public string? AgentVersion { get; init; }
    public IReadOnlyList<EdgeSignalDto> Signals { get; init; } = [];
}

public sealed record EdgeSignalBatchResponse
{
    /// <summary>The highest sequence applied; the edge may drop everything up to it.</summary>
    public long LastSequence { get; init; }
    public int Applied { get; init; }

    /// <summary>Signals already applied before (a batch sent again), or for tags no longer bound.</summary>
    public int Skipped { get; init; }
}

public sealed record EdgeMetricReportDto
{
    public Guid ConnectionId { get; init; }
    public string Address { get; init; } = "";
    public string? DataType { get; init; }
    public string? Value { get; init; }
    public DateTimeOffset SeenAt { get; init; }
}

public sealed record EdgeMetricsRequest
{
    public IReadOnlyList<EdgeMetricReportDto> Metrics { get; init; } = [];
}
