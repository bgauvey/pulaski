namespace Pulaski.Contracts.WorkOrders;

/// <summary>One work order as the ERP sends it (D-066). Codes are matched in Pulaski: the site, material, unit and (optionally) process.</summary>
public sealed record ErpWorkOrderRequest
{
    public string ExternalSystem { get; init; } = "";
    public string ExternalKey { get; init; } = "";

    /// <summary>The ERP's version of the order; the same version again changes nothing.</summary>
    public string? ExternalVersion { get; init; }
    public string SiteCode { get; init; } = "";
    public string OrderNumber { get; init; } = "";
    public string MaterialCode { get; init; } = "";
    public decimal Quantity { get; init; }
    public string UomCode { get; init; } = "";
    public DateTimeOffset PlannedStart { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int? Priority { get; init; }

    /// <summary>The process to make it from; blank uses the material's default process, else the only one at the site that makes it.</summary>
    public string? ProcessCode { get; init; }
    public string? Notes { get; init; }

    /// <summary>The ERP cancelled the order.</summary>
    public bool Cancelled { get; init; }
}

public sealed record ImportWorkOrdersRequest
{
    public IReadOnlyList<ErpWorkOrderRequest> Orders { get; init; } = [];
}

/// <summary>What became of one order: <c>created</c>, <c>updated</c>, <c>unchanged</c>, <c>pending</c> (waits for a planner), <c>cancelled</c> or <c>refused</c>.</summary>
public sealed record ErpOrderResultDto
{
    public required string ExternalKey { get; init; }
    public required string OrderNumber { get; init; }
    public required string Action { get; init; }
    public Guid? WorkOrderId { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];
}

public sealed record ImportWorkOrdersResponse
{
    public IReadOnlyList<ErpOrderResultDto> Results { get; init; } = [];
}

/// <summary>A change from the ERP to a dispatched order, waiting for a planner (D-066).</summary>
public sealed record ErpPendingChangeDto
{
    public decimal Quantity { get; init; }
    public required string UomCode { get; init; }
    public DateTimeOffset PlannedStart { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int Priority { get; init; }
    public string? Version { get; init; }
    public bool Cancelled { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}

public sealed record DecideErpChangeRequest
{
    public long Version { get; init; }

    /// <summary>Why, for a rejection (required then).</summary>
    public string? Reason { get; init; }
}

/// <summary>A held order's whole-batch quantity, in its unit (D-066).</summary>
public sealed record SetWorkOrderQuantityRequest
{
    public long Version { get; init; }
    public decimal Quantity { get; init; }
}

/// <summary>A rule for ERP quantities that aren't whole batches (D-066); for a material, blank uses the site's.</summary>
public sealed record SetErpBatchRuleRequest
{
    public long Version { get; init; }
    public string? Rule { get; init; }
}
