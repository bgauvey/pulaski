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
