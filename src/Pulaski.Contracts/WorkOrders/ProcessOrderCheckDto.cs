namespace Pulaski.Contracts.WorkOrders;

/// <summary>
/// Whether a work order for a material can be made from a process (D-024), checked the same way
/// creating the order checks it. Empty <see cref="Problems"/> means it can.
/// </summary>
public sealed record ProcessOrderCheckDto
{
    public required Guid ProcessId { get; init; }
    public required Guid MaterialId { get; init; }

    /// <summary>The released version checked; null when the process has none.</summary>
    public Guid? ProcessVersionId { get; init; }

    public IReadOnlyList<ProcessOrderProblemDto> Problems { get; init; } = [];

    /// <summary>Operations on a department; their jobs finish by the order's due date, so the order needs one (D-022).</summary>
    public IReadOnlyList<int> DepartmentOperations { get; init; } = [];

    /// <summary>Where each operation's share of the order would go, with each line's own batch size (#85), for the form's batch hint.</summary>
    public IReadOnlyList<OrderEquipmentBatchDto> Equipment { get; init; } = [];
}

/// <summary>
/// One equipment of an operation: its share of the order and, on a line with one, its own batch size (D-065) in the material's base unit;
/// null for the material's size. A department plans in the material's size until a line is chosen.
/// </summary>
public sealed record OrderEquipmentBatchDto(int OperationNumber, string EquipmentCode, decimal SharePercent, bool IsDepartment, decimal? LineBatchSize);

/// <summary>
/// Why one operation can't run where the process says. <see cref="Code"/> is
/// <c>no_released_version</c>, <c>inactive</c>, <c>not_covered</c>, <c>cannot_take_jobs</c> or
/// <c>no_capability</c>; the operation fields are empty for problems with the whole process.
/// </summary>
public sealed record ProcessOrderProblemDto(string Code, string Message, int? OperationNumber = null, string? EquipmentCode = null);
