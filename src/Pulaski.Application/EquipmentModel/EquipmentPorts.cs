using Pulaski.Application.Abstractions;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Application.EquipmentModel;

/// <summary>Read side: DTOs straight from SQL, no locks, no transaction.</summary>
public interface IEquipmentReadStore
{
    /// <summary>The hierarchy in tree order (each row after its parent).</summary>
    Task<IReadOnlyList<EquipmentDto>> ListAsync(Guid? siteId, CancellationToken cancellationToken);

    Task<EquipmentDto?> GetAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IEquipmentRepository
{
    Task<Versioned<Equipment>?> GetAsync(Guid id, RowLock rowLock, CancellationToken cancellationToken);

    /// <summary>Inserts the row and its closure-table entries.</summary>
    /// <returns>The new row version.</returns>
    Task<long> InsertAsync(Equipment equipment, DateTimeOffset at, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> UpdateAsync(Equipment equipment, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>True when <paramref name="descendantId"/> is <paramref name="ancestorId"/> or sits below it.</summary>
    Task<bool> IsInSubtreeAsync(Guid ancestorId, Guid descendantId, CancellationToken cancellationToken);
}
