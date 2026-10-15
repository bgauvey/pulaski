using Pulaski.Application.Abstractions;
using Pulaski.Application.Processes;
using Pulaski.Contracts.Processes;
using Pulaski.Domain.Processes;

namespace Pulaski.Testing;

/// <summary>In-memory process routes (D-023). Equipment checks see what <see cref="AddEquipment"/> registered.</summary>
public sealed class InMemoryProcesses : IProcessReadStore, IProcessRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<Process>> _processes = [];
    private readonly Dictionary<Guid, Versioned<ProcessVersion>> _versions = [];
    private readonly Dictionary<Guid, ProcessEquipmentRow> _equipment = [];
    private readonly Dictionary<Guid, Guid?> _materialDefaults = [];
    private readonly Dictionary<Guid, ProcessScope> _scopes = [];
    private readonly Dictionary<Guid, Guid?> _materialClasses = [];
    private long _next = 1;

    /// <summary>Released versions written out as rows, by version id.</summary>
    public HashSet<Guid> ReleasedRows { get; } = [];

    public void AddEquipment(ProcessEquipmentRow row)
    {
        lock (_lock)
        {
            _equipment[row.Id] = row;
        }
    }

    public void AddMaterial(Guid materialId, Guid? materialClassId = null)
    {
        lock (_lock)
        {
            _materialDefaults[materialId] = null;
            _materialClasses[materialId] = materialClassId;
        }
    }

    public ProcessScope ScopeOf(Guid processId)
    {
        lock (_lock)
        {
            return _scopes.GetValueOrDefault(processId, ProcessScope.Nothing);
        }
    }

    public Task<IReadOnlyList<ProcessSummaryDto>> ListAsync(Guid? siteId, Guid? materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ProcessSummaryDto>>([.. _processes.Values
                .Select(p => p.Value)
                .Where(p => siteId is null || p.SiteId == siteId)
                .Where(p => materialId is not { } m || Covers(p.Id, m))
                .OrderBy(p => p.Code, StringComparer.Ordinal)
                .Select(p => new ProcessSummaryDto
                {
                    Id = p.Id,
                    SiteId = p.SiteId,
                    Code = p.Code,
                    Name = p.Name,
                    Description = p.Description,
                    IsActive = p.IsActive,
                    ReleasedVersion = VersionsOf(p.Id).FirstOrDefault(v => v.Status == ProcessVersionStatus.Released)?.Number,
                    DraftVersion = VersionsOf(p.Id).FirstOrDefault(v => v.Status == ProcessVersionStatus.Draft)?.Number,
                })]);
        }
    }

    public Task<ProcessDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_processes.TryGetValue(id, out var row))
            {
                return Task.FromResult<ProcessDto?>(null);
            }

            var p = row.Value;
            return Task.FromResult<ProcessDto?>(new ProcessDto
            {
                Id = p.Id,
                SiteId = p.SiteId,
                SiteCode = "PLT1",
                Code = p.Code,
                Name = p.Name,
                Description = p.Description,
                IsActive = p.IsActive,
                Version = row.Version,
                Versions = [.. VersionsOf(id).OrderByDescending(v => v.Number)
                    .Select(v => new ProcessVersionSummaryDto(v.Id, v.Number, v.Status, v.ReleasedAt, v.Note))],
                DefaultFor = [.. _materialDefaults.Where(m => m.Value == id).Select(m => new ProcessMaterialDto(m.Key, "M", "Material"))],
            });
        }
    }

    public Task<ProcessVersionDto?> GetVersionAsync(Guid processId, Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_versions.TryGetValue(versionId, out var row) || row.Value.ProcessId != processId)
            {
                return Task.FromResult<ProcessVersionDto?>(null);
            }

            var v = row.Value;
            return Task.FromResult<ProcessVersionDto?>(new ProcessVersionDto
            {
                Id = v.Id,
                ProcessId = v.ProcessId,
                Number = v.Number,
                Status = v.Status,
                ReleasedAt = v.ReleasedAt,
                Note = v.Note,
                Definition = ToDto(v.Definition),
                Version = row.Version,
            });
        }
    }

    public Task<IReadOnlyList<ProcessEquipmentRow>> GetEquipmentAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ProcessEquipmentRow>>([.. ids.Where(_equipment.ContainsKey).Select(id => _equipment[id])]);
        }
    }

    public Task<Versioned<Process>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_processes.GetValueOrDefault(id));
        }
    }

    public Task<long> InsertAsync(Process process, DateTimeOffset at, CancellationToken cancellationToken) => Put(_processes, process.Id, process);

    public Task<long> UpdateAsync(Process process, DateTimeOffset at, CancellationToken cancellationToken) => Put(_processes, process.Id, process);

    public Task<Versioned<ProcessVersion>?> GetVersionForUpdateAsync(Guid processId, Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.TryGetValue(versionId, out var row) && row.Value.ProcessId == processId ? row : null);
        }
    }

    public Task<Versioned<ProcessVersion>?> GetReleasedForUpdateAsync(Guid processId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.Values.FirstOrDefault(v => v.Value.ProcessId == processId && v.Value.Status == ProcessVersionStatus.Released));
        }
    }

    public Task<bool> HasDraftAsync(Guid processId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(VersionsOf(processId).Any(v => v.Status == ProcessVersionStatus.Draft));
        }
    }

    public Task<long> InsertVersionAsync(ProcessVersion version, DateTimeOffset at, CancellationToken cancellationToken) => Put(_versions, version.Id, version);

    public Task<long> UpdateVersionAsync(ProcessVersion version, DateTimeOffset at, CancellationToken cancellationToken) => Put(_versions, version.Id, version);

    public Task InsertReleasedRowsAsync(ProcessVersion version, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ReleasedRows.Add(version.Id);
        }

        return Task.CompletedTask;
    }

    public Task<long> SetScopeAsync(Guid processId, ProcessScope scope, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _scopes[processId] = scope;
        }

        return Put(_processes, processId, _processes[processId].Value);
    }

    /// <summary>Everything exists here; the Infrastructure tests check the real lookup.</summary>
    public Task<(int Classes, int Materials)> CountExistingAsync(
        IReadOnlyCollection<Guid> classIds, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken) =>
        Task.FromResult((classIds.Count, materialIds.Count));

    public Task<bool> CoversAsync(Guid processId, Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Covers(processId, materialId));
        }
    }

    private bool Covers(Guid processId, Guid materialId) =>
        _scopes.GetValueOrDefault(processId, ProcessScope.Nothing).Covers(materialId, _materialClasses.GetValueOrDefault(materialId));

    public Task<long?> SetMaterialDefaultAsync(Guid materialId, Guid? processId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_materialDefaults.ContainsKey(materialId))
            {
                return Task.FromResult<long?>(null);
            }

            _materialDefaults[materialId] = processId;
            return Task.FromResult<long?>(++_next);
        }
    }

    private IEnumerable<ProcessVersion> VersionsOf(Guid processId) => _versions.Values.Select(v => v.Value).Where(v => v.ProcessId == processId);

    private Task<long> Put<T>(Dictionary<Guid, Versioned<T>> rows, Guid id, T value)
    {
        lock (_lock)
        {
            var version = ++_next;
            rows[id] = new Versioned<T>(value, version);
            return Task.FromResult(version);
        }
    }

    private static ProcessDefinitionDto ToDto(ProcessDefinition d) => new()
    {
        Operations = [.. d.Operations.Select(o => new ProcessOperationDto
        {
            Id = o.Id,
            Number = o.Number,
            Name = o.Name,
            Description = o.Description,
            X = o.X,
            Y = o.Y,
            Equipment = [.. o.Equipment.Select(e => new OperationEquipmentDto(e.EquipmentId, e.SharePercent))],
        })],
        Dependencies = [.. d.Dependencies.Select(x => new ProcessDependencyDto(x.Id, x.FromOperationId, x.ToOperationId, x.Type, x.Lag))],
    };
}
