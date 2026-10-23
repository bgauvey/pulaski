using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Contracts.Boms;
using Pulaski.Domain.Boms;

namespace Pulaski.Testing;

/// <summary>In-memory bills of materials (D-025). Materials checks see what <see cref="AddMaterial"/> registered.</summary>
public sealed class InMemoryBoms : IBomReadStore, IBomRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<Bom>> _boms = [];
    private readonly Dictionary<Guid, Versioned<BomVersion>> _versions = [];
    private readonly Dictionary<Guid, BomMaterialRow> _materials = [];
    private readonly List<JobMaterial> _jobMaterials = [];
    private long _next = 1;

    /// <summary>Released versions written out as rows, by version id.</summary>
    public HashSet<Guid> ReleasedRows { get; } = [];

    public IReadOnlyList<PlannedComponentDto> PlannedComponents { get; set; } = [];

    public IReadOnlyList<JobMaterial> JobMaterials
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobMaterials];
            }
        }
    }

    public BomMaterialRow AddMaterial(Guid id, Guid baseUomId, bool isActive = true, decimal? batchSize = null, string batchRule = "near")
    {
        var row = new BomMaterialRow
        {
            Id = id,
            Code = "M-" + id.ToString("N")[^4..],
            BaseUomId = baseUomId,
            IsActive = isActive,
            BatchSize = batchSize,
            BatchRule = batchSize is null ? null : batchRule,
        };
        lock (_lock)
        {
            _materials[id] = row;
        }

        return row;
    }

    /// <summary>An active BOM with <paramref name="definition"/> released as version 1.</summary>
    public BomVersion AddReleased(Guid materialId, Guid? processId, BomDefinition definition)
    {
        var bom = new Bom(Guid.CreateVersion7(), materialId, processId, true);
        var version = new BomVersion(Guid.CreateVersion7(), bom.Id, 1, BomVersionStatus.Released, definition, DateTimeOffset.UnixEpoch, null);
        Put(_boms, bom.Id, bom);
        Put(_versions, version.Id, version);
        return version;
    }

    public Versioned<BomVersion>? FindVersion(Guid id)
    {
        lock (_lock)
        {
            return _versions.GetValueOrDefault(id);
        }
    }

    public Task<Versioned<Bom>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_boms.GetValueOrDefault(id));
        }
    }

    public Task<long> InsertAsync(Bom bom, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_boms.Values.Any(b => b.Value.MaterialId == bom.MaterialId && b.Value.ProcessId == bom.ProcessId))
            {
                throw new Application.Errors.ConflictException("A BOM for that material and process already exists.");
            }
        }

        return Put(_boms, bom.Id, bom);
    }

    public Task<Versioned<BomVersion>?> GetVersionForUpdateAsync(Guid bomId, Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.TryGetValue(versionId, out var row) && row.Value.BomId == bomId ? row : null);
        }
    }

    public Task<Versioned<BomVersion>?> GetReleasedForUpdateAsync(Guid bomId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.Values.FirstOrDefault(v => v.Value.BomId == bomId && v.Value.Status == BomVersionStatus.Released));
        }
    }

    public Task<bool> HasDraftAsync(Guid bomId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.Values.Any(v => v.Value.BomId == bomId && v.Value.Status == BomVersionStatus.Draft));
        }
    }

    public Task<BomVersion?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.GetValueOrDefault(versionId)?.Value);
        }
    }

    /// <summary>Answers <see cref="ListMissingOperationsAsync"/>; tests set the rows they need.</summary>
    public List<BomMissingOperationsRow> MissingOperations { get; } = [];

    public Task<IReadOnlyList<BomMissingOperationsRow>> ListMissingOperationsAsync(Guid processId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BomMissingOperationsRow>>([.. MissingOperations]);

    public Task<long> InsertVersionAsync(BomVersion version, DateTimeOffset at, CancellationToken cancellationToken) => Put(_versions, version.Id, version);

    public Task<long> UpdateVersionAsync(BomVersion version, DateTimeOffset at, CancellationToken cancellationToken) => Put(_versions, version.Id, version);

    public Task InsertReleasedRowsAsync(BomVersion version, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ReleasedRows.Add(version.Id);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BomMaterialRow>> GetMaterialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<BomMaterialRow>>([.. ids.Where(_materials.ContainsKey).Select(id => _materials[id])]);
        }
    }

    public Task<BomVersion?> GetReleasedForOrderAsync(Guid materialId, Guid processId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var released = _versions.Values.Select(v => v.Value).Where(v => v.Status == BomVersionStatus.Released)
                .Select(v => (Version: v, Bom: _boms[v.BomId].Value))
                .Where(x => x.Bom.IsActive && x.Bom.MaterialId == materialId && (x.Bom.ProcessId == processId || x.Bom.ProcessId is null))
                .OrderBy(x => x.Bom.ProcessId is null)
                .Select(x => x.Version)
                .FirstOrDefault();
            return Task.FromResult(released);
        }
    }

    public Task InsertJobMaterialsAsync(IReadOnlyList<JobMaterial> rows, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobMaterials.AddRange(rows);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BomSummaryDto>> ListAsync(Guid? materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<BomSummaryDto>>([.. _boms.Values.Select(b => b.Value)
                .Where(b => materialId is null || b.MaterialId == materialId)
                .Select(b => new BomSummaryDto
                {
                    Id = b.Id,
                    MaterialId = b.MaterialId,
                    MaterialCode = "MAT",
                    MaterialName = "Material",
                    ProcessId = b.ProcessId,
                    IsActive = b.IsActive,
                    ReleasedVersion = VersionsOf(b.Id).FirstOrDefault(v => v.Status == BomVersionStatus.Released)?.Number,
                    DraftVersion = VersionsOf(b.Id).FirstOrDefault(v => v.Status == BomVersionStatus.Draft)?.Number,
                })]);
        }
    }

    public Task<BomDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_boms.TryGetValue(id, out var row))
            {
                return Task.FromResult<BomDto?>(null);
            }

            var b = row.Value;
            return Task.FromResult<BomDto?>(new BomDto
            {
                Id = b.Id,
                MaterialId = b.MaterialId,
                MaterialCode = "MAT",
                MaterialName = "Material",
                MaterialBaseUomId = _materials.GetValueOrDefault(b.MaterialId)?.BaseUomId ?? Guid.Empty,
                ProcessId = b.ProcessId,
                IsActive = b.IsActive,
                Version = row.Version,
                Versions = [.. VersionsOf(id).OrderByDescending(v => v.Number)
                    .Select(v => new BomVersionSummaryDto(v.Id, v.Number, v.Status, v.ReleasedAt, v.Note))],
            });
        }
    }

    public Task<BomVersionDto?> GetVersionAsync(Guid bomId, Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_versions.TryGetValue(versionId, out var row) || row.Value.BomId != bomId)
            {
                return Task.FromResult<BomVersionDto?>(null);
            }

            var v = row.Value;
            return Task.FromResult<BomVersionDto?>(new BomVersionDto
            {
                Id = v.Id,
                BomId = v.BomId,
                Number = v.Number,
                Status = v.Status,
                ReleasedAt = v.ReleasedAt,
                Note = v.Note,
                Version = row.Version,
                Definition = new BomDefinitionDto
                {
                    BaseQty = v.Definition.BaseQty,
                    BaseUomId = v.Definition.BaseUomId,
                    Items = [.. v.Definition.Items.Select(i => new BomItemDto
                    {
                        Id = i.Id,
                        LineNo = i.LineNo,
                        ComponentMaterialId = i.ComponentMaterialId,
                        QtyPer = i.QtyPer,
                        UomId = i.UomId,
                        ScrapFactor = i.ScrapFactor,
                        ConsumptionMethod = i.ConsumptionMethod,
                        OperationNumber = i.OperationNumber,
                    })],
                },
            });
        }
    }

    public Task<IReadOnlyList<PlannedComponentDto>> ListPlannedComponentsAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(PlannedComponents);

    private IEnumerable<BomVersion> VersionsOf(Guid bomId) => _versions.Values.Select(v => v.Value).Where(v => v.BomId == bomId);

    private Task<long> Put<T>(Dictionary<Guid, Versioned<T>> rows, Guid id, T value)
    {
        lock (_lock)
        {
            var version = ++_next;
            rows[id] = new Versioned<T>(value, version);
            return Task.FromResult(version);
        }
    }
}
