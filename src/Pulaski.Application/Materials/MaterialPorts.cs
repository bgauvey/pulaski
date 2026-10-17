using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Materials;

/// <summary>Read side for materials management (D-033). The lists come from the reference-data store.</summary>
public interface IMaterialReadStore
{
    /// <summary>The material with what uses it and where it can run; null when it doesn't exist.</summary>
    Task<MaterialDetailDto?> GetDetailAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IMaterialAdminRepository
{
    Task<Versioned<Material>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The material an external system knows by <paramref name="externalKey"/>, locked.</summary>
    Task<Versioned<Material>?> FindByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertAsync(Material material, DateTimeOffset at, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> UpdateAsync(Material material, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Whether anything refers to the material, and whether it has stock on hand (D-033).</summary>
    Task<MaterialUse> GetUseAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>What still uses the material, in words, for a deactivation to report.</summary>
    Task<IReadOnlyList<string>> DescribeUseAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A unit read <c>FOR SHARE</c>: its id and whether it is active; null when there is no such code.</summary>
    Task<(Guid Id, bool IsActive)?> FindUomAsync(string code, CancellationToken cancellationToken);

    /// <summary>True when the unit exists and is active.</summary>
    Task<bool> IsUomActiveAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A class read <c>FOR SHARE</c>; null when it doesn't exist.</summary>
    Task<MaterialClass?> GetClassAsync(Guid id, CancellationToken cancellationToken);

    Task<Versioned<MaterialClass>?> GetClassForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertClassAsync(MaterialClass materialClass, DateTimeOffset at, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> UpdateClassAsync(MaterialClass materialClass, DateTimeOffset at, CancellationToken cancellationToken);
}
