using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;

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

    /// <summary>The material with this code, locked; null when there is none.</summary>
    Task<Versioned<Material>?> FindByCodeForUpdateAsync(string code, CancellationToken cancellationToken);

    /// <summary>The class with this code, read <c>FOR SHARE</c>; null when there is none.</summary>
    Task<MaterialClass?> FindClassByCodeAsync(string code, CancellationToken cancellationToken);

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

    /// <summary>The material's batch settings (D-036): null size when it is made continuously.</summary>
    Task<(decimal? Size, decimal TolerancePct, string? Rule)> GetBatchingAsync(Guid id, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> UpdateBatchingAsync(Guid id, decimal? size, decimal tolerancePct, string? rule, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The material's work orders that aren't completed, closed or cancelled, with their quantities.</summary>
    Task<IReadOnlyList<(string OrderNumber, decimal Quantity, Guid UomId)>> ListOpenOrdersAsync(Guid id, CancellationToken cancellationToken);

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

/// <summary>A line as a capability change sees it, locked <c>FOR UPDATE</c> like every scheduling write (D-017).</summary>
public sealed record CapabilityLine(Guid Id, string Code, bool CanRunJob, bool IsActive);

/// <summary>A line an import names by code, with its site (equipment codes are unique only within a site).</summary>
public sealed record SiteLine(CapabilityLine Line, Guid? SiteId, string? SiteCode);

/// <summary>Write side for capabilities and the run standards they seed (D-020, D-034); runs inside the current <see cref="IUnitOfWork"/>.</summary>
/// <summary>
/// The batch sizes lines have of their own for a material (D-065): from each line's capability in effect at a time, in the material's
/// base unit. A line missing from the answer uses the material's size. Read in the command's transaction, after the lines are locked.
/// </summary>
public interface ILineBatchSizes
{
    Task<IReadOnlyDictionary<Guid, decimal>> GetAsync(Guid materialId, IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset at, CancellationToken cancellationToken);
}

public interface ICapabilityRepository
{
    /// <summary>The line, locked <c>FOR UPDATE</c>; null when it doesn't exist.</summary>
    Task<CapabilityLine?> LockLineAsync(Guid equipmentId, CancellationToken cancellationToken);

    /// <summary>
    /// The equipment with these codes (compared in lower case) at every site, locked <c>FOR UPDATE</c> in id order in SQL, so a
    /// capability import takes its lines before any material, as every scheduling write does (D-017, D-055).
    /// </summary>
    Task<IReadOnlyList<SiteLine>> LockLinesByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>The capability's line, without locking; null when the capability doesn't exist.</summary>
    Task<Guid?> GetLineOfAsync(Guid capabilityId, CancellationToken cancellationToken);

    Task<Versioned<Capability>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The material's other capabilities on the line, for the no-overlap rule.</summary>
    Task<IReadOnlyList<Capability>> ListOnLineAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertAsync(Capability capability, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> UpdateAsync(Capability capability, CancellationToken cancellationToken);

    /// <summary>The line's run standard for the material, locked; null when it has none.</summary>
    Task<Versioned<RunStandard>?> GetRunStandardForUpdateAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken);

    Task InsertRunStandardAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the calculated rate, its basis and unit; the modified rate belongs to people (D-016).</summary>
    Task UpdateRunStandardCalculationAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Open production jobs for the material on the line (not completed, closed or cancelled).</summary>
    Task<int> CountOpenJobsAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken);
}
