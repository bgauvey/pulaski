namespace Pulaski.Domain.WorkOrders;

/// <summary>
/// A work order (ISA-95 work request): a quantity of one material (D-003). One made from a process
/// keeps the released version it came from (D-024), and the BOM version its components were
/// planned with (D-025); <see cref="Notes"/> is the planner's note.
/// </summary>
public sealed record WorkOrder(
    Guid Id,
    Guid SiteId,
    string OrderNumber,
    Guid MaterialId,
    decimal PlannedQty,
    Guid UomId,
    Guid? TargetEquipmentId,
    DateTimeOffset? PlannedStart,
    DateTimeOffset? PlannedEnd,
    DateTimeOffset? DueAt,
    int Priority,
    string Status,
    string Origin,
    Guid? ProcessVersionId = null,
    string? Notes = null,
    Guid? BomVersionId = null)
{
    public const string LocalOrigin = "local";

    /// <summary>A work order entered in Pulaski rather than imported from the ERP.</summary>
    /// <exception cref="DomainRuleException">The quantity or planned window is invalid.</exception>
    public static WorkOrder CreateLocal(
        Guid id,
        Guid siteId,
        string orderNumber,
        Guid materialId,
        decimal plannedQty,
        Guid uomId,
        Guid? targetEquipmentId,
        DateTimeOffset? plannedStart,
        DateTimeOffset? plannedEnd,
        DateTimeOffset? dueAt,
        int priority,
        Guid? processVersionId = null,
        string? notes = null,
        Guid? bomVersionId = null)
    {
        if (plannedQty <= 0)
        {
            throw new DomainRuleException("Planned quantity must be greater than zero.");
        }

        if (plannedStart is not null && plannedEnd is not null && plannedEnd <= plannedStart)
        {
            throw new DomainRuleException("Planned end must be after planned start.");
        }

        return new WorkOrder(
            id, siteId, orderNumber, materialId, plannedQty, uomId, targetEquipmentId,
            plannedStart, plannedEnd, dueAt, priority, WorkOrderStatus.Planned, LocalOrigin,
            processVersionId, string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), bomVersionId);
    }

    /// <exception cref="DomainRuleException">The transition is not allowed.</exception>
    public WorkOrder WithStatus(string status)
    {
        WorkOrderStatus.EnsureCanTransition(Status, status);
        return this with { Status = status };
    }
}
