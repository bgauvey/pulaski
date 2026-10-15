using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Processes;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Processes;

/// <summary>Read side for process routes (D-023): DTOs straight from SQL, no locks.</summary>
public interface IProcessReadStore
{
    /// <summary>Processes in the site (all sites when null) whose scope covers <paramref name="materialId"/> (any when null).</summary>
    Task<IReadOnlyList<ProcessSummaryDto>> ListAsync(Guid? siteId, Guid? materialId, CancellationToken cancellationToken);

    /// <summary>The process with its versions (newest first) and the materials it is the default for.</summary>
    Task<ProcessDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One version with its definition; <see cref="ProcessVersionDto.Problems"/> is not filled.</summary>
    Task<ProcessVersionDto?> GetVersionAsync(Guid processId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>The equipment a definition names, for checking it can be used.</summary>
    Task<IReadOnlyList<ProcessEquipmentRow>> GetEquipmentAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>. Commands lock the process, then its version.</summary>
public interface IProcessRepository
{
    Task<Versioned<Process>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertAsync(Process process, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(Process process, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<ProcessVersion>?> GetVersionForUpdateAsync(Guid processId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>The process's released version, if it has one, locked.</summary>
    Task<Versioned<ProcessVersion>?> GetReleasedForUpdateAsync(Guid processId, CancellationToken cancellationToken);

    Task<bool> HasDraftAsync(Guid processId, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertVersionAsync(ProcessVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the status, definition, release time and note.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateVersionAsync(ProcessVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes a released version's operations, their equipment and the links as fixed rows (D-010).</summary>
    Task InsertReleasedRowsAsync(ProcessVersion version, CancellationToken cancellationToken);

    /// <summary>The equipment a definition names, read inside the transaction.</summary>
    Task<IReadOnlyList<ProcessEquipmentRow>> GetEquipmentAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Replaces the scope: sets the all-materials flag and switches the class and material rows on or off.</summary>
    /// <returns>The process's new row version.</returns>
    Task<long> SetScopeAsync(Guid processId, ProcessScope scope, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>How many of the material classes and materials exist, to check a scope names only real ones.</summary>
    Task<(int Classes, int Materials)> CountExistingAsync(IReadOnlyCollection<Guid> classIds, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken);

    /// <summary>True when the process's scope covers the material.</summary>
    Task<bool> CoversAsync(Guid processId, Guid materialId, CancellationToken cancellationToken);

    /// <summary>Sets or clears the material's default process; the material's new row version, or null when it doesn't exist.</summary>
    Task<long?> SetMaterialDefaultAsync(Guid materialId, Guid? processId, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed record ProcessEquipmentRow
{
    public required Guid Id { get; init; }
    public Guid? SiteId { get; init; }
    public required string Code { get; init; }
    public required string Level { get; init; }
    public bool IsSchedulable { get; init; }
    public bool CanRunJob { get; init; }
    public bool IsActive { get; init; }
}
