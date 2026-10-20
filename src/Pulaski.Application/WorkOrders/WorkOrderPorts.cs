using Pulaski.Application.Abstractions;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.WorkOrders;

/// <summary>Read side: DTOs straight from SQL, no locks, no transaction.</summary>
public interface IWorkOrderReadStore
{
    /// <summary>Work orders by due date, then priority. <see cref="WorkOrderDto.NextStatuses"/> is not filled.</summary>
    Task<IReadOnlyList<WorkOrderDto>> ListAsync(Guid? siteId, string? status, CancellationToken cancellationToken);

    Task<WorkOrderDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Jobs and the site time zone needed to print a work order's traveler sheets.</summary>
    Task<ShopOrderJobsRow?> GetShopOrderJobsAsync(Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// The equipment at and below <paramref name="equipmentId"/> (all equipment when null), each
    /// marked when it holds jobs (a line, or a group that plans for lines). Empty when the
    /// equipment does not exist.
    /// </summary>
    Task<IReadOnlyList<ScopeEquipmentRow>> GetScopeAsync(Guid? equipmentId, CancellationToken cancellationToken);

    /// <summary>Per work order in the site (all sites when null): good quantity made and the number of open jobs.</summary>
    Task<IReadOnlyList<WorkOrderFactsRow>> GetFactsAsync(Guid? siteId, CancellationToken cancellationToken);
}

public sealed record ScopeEquipmentRow
{
    public required Guid Id { get; init; }
    public Guid? SiteId { get; init; }
    public bool HoldsJobs { get; init; }
}

public sealed record WorkOrderFactsRow
{
    public required Guid WorkOrderId { get; init; }
    public decimal CompletedQty { get; init; }
    public int OpenJobs { get; init; }
}

public sealed record ShopOrderJobsRow
{
    public required string SiteTimeZone { get; init; }
    public IReadOnlyList<ShopOrderJobRow> Jobs { get; init; } = [];
}

public sealed record ShopOrderJobRow
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public bool IsGroup { get; init; }
    public decimal? PlannedQty { get; init; }
    public string? UomCode { get; init; }
    public Guid? MaterialId { get; init; }
    public Guid? UomId { get; init; }
    public int? OperationNumber { get; init; }
    public string? OperationName { get; init; }
    public string? OperationDescription { get; init; }
    public DateTimeOffset? BaselineStart { get; init; }
    public DateTimeOffset? BaselineEnd { get; init; }
    public decimal? RatePerHour { get; init; }
    public string? RateUomCode { get; init; }
    public string? RateSource { get; init; }
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IWorkOrderRepository
{
    Task<Versioned<WorkOrder>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Reads the work order <c>FOR SHARE</c>, for a command that references it (e.g. a job).</summary>
    Task<Versioned<WorkOrder>?> GetForShareAsync(Guid id, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertAsync(WorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>True while any of the work order's jobs is still open.</summary>
    Task<bool> HasOpenJobsAsync(Guid id, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> UpdateStatusAsync(Guid id, string status, DateTimeOffset at, CancellationToken cancellationToken);
}
