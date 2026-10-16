namespace Pulaski.Contracts.WorkOrders;

/// <summary>
/// A new work order made from a process (D-024). The process's released version assigns the
/// equipment: its operations become the order's jobs.
/// </summary>
public sealed record CreateWorkOrderRequest
{
    public Guid ProcessId { get; init; }
    public string OrderNumber { get; init; } = "";
    public Guid MaterialId { get; init; }
    public decimal PlannedQty { get; init; }
    public Guid UomId { get; init; }

    /// <summary>When the order starts: no job runs before it.</summary>
    public DateTimeOffset PlannedStart { get; init; }

    /// <summary>The due date; also the finish-by of a job planned to a department.</summary>
    public DateTimeOffset? DueAt { get; init; }

    public int Priority { get; init; }

    /// <summary>The planner's note on the order; at most 4000 characters.</summary>
    public string? Notes { get; init; }
}

public sealed record ChangeWorkOrderStatusRequest
{
    public long Version { get; init; }
    public string Status { get; init; } = "";
    public string? Reason { get; init; }
}
