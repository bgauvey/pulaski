using Pulaski.Application.Abstractions;
using Pulaski.Application.Quality;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;

namespace Pulaski.Testing;

/// <summary>
/// In-memory quality (#65): characteristics, specifications and results. A test adds materials (<see cref="Materials"/>), jobs
/// (<see cref="Jobs"/>) and lot productions it needs; the Infrastructure tests cover the real SQL.
/// </summary>
public sealed class InMemoryQuality : IQualityRepository, IQualityReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (Characteristic Value, long Version)> _characteristics = [];
    private readonly Dictionary<Guid, (MaterialSpec Value, long Version)> _specs = [];
    private readonly Dictionary<Guid, (SpecVersion Value, long Version)> _versions = [];
    private readonly Dictionary<Guid, (SpecItem Item, Guid VersionId)> _released = [];
    private readonly List<QualityResultRow> _results = [];
    private long _next = 1;

    public Dictionary<Guid, string> Materials { get; } = [];

    public Dictionary<Guid, QualityJobContext> Jobs { get; } = [];

    public Dictionary<Guid, LotProduction> Productions { get; } = [];

    /// <summary>Reasons by (kind, code), as <see cref="FindReasonAsync"/> finds them.</summary>
    public Dictionary<(string Kind, string Code), Guid> Reasons { get; } = [];

    public IReadOnlyList<QualityResultRow> Results
    {
        get
        {
            lock (_lock)
            {
                return [.. _results];
            }
        }
    }

    public Task<Versioned<Characteristic>?> GetCharacteristicForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_characteristics.TryGetValue(id, out var c) ? new Versioned<Characteristic>(c.Value, c.Version) : null);
        }
    }

    public Task<IReadOnlyList<Characteristic>> GetCharacteristicsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Characteristic>>([.. ids.Where(_characteristics.ContainsKey).Select(id => _characteristics[id].Value)]);
        }
    }

    public Task<bool> CharacteristicCodeExistsAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_characteristics.Values.Any(c => c.Value.Code == code));
        }
    }

    public Task<long> InsertCharacteristicAsync(Characteristic characteristic, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpdateCharacteristicAsync(characteristic, at, cancellationToken);

    public Task<long> UpdateCharacteristicAsync(Characteristic characteristic, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _next++;
            _characteristics[characteristic.Id] = (characteristic, version);
            return Task.FromResult(version);
        }
    }

    public Task<(string Code, bool IsActive)?> GetMaterialAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<(string, bool)?>(Materials.TryGetValue(id, out var code) ? (code, true) : null);

    public Task<bool> SpecExistsForMaterialAsync(Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_specs.Values.Any(s => s.Value.MaterialId == materialId));
        }
    }

    public Task<Versioned<MaterialSpec>?> GetSpecForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_specs.TryGetValue(id, out var s) ? new Versioned<MaterialSpec>(s.Value, s.Version) : null);
        }
    }

    public Task<long> InsertSpecAsync(MaterialSpec spec, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _next++;
            _specs[spec.Id] = (spec, version);
            return Task.FromResult(version);
        }
    }

    public Task<Versioned<SpecVersion>?> GetVersionForUpdateAsync(Guid specId, Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.TryGetValue(versionId, out var v) && v.Value.SpecId == specId ? new Versioned<SpecVersion>(v.Value, v.Version) : null);
        }
    }

    public Task<Versioned<SpecVersion>?> GetReleasedForUpdateAsync(Guid specId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var released = _versions.Values.FirstOrDefault(v => v.Value.SpecId == specId && v.Value.Status == SpecVersionStatus.Released);
            return Task.FromResult(released.Value is null ? null : new Versioned<SpecVersion>(released.Value, released.Version));
        }
    }

    public Task<bool> HasDraftAsync(Guid specId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.Values.Any(v => v.Value.SpecId == specId && v.Value.Status == SpecVersionStatus.Draft));
        }
    }

    public Task<long> InsertVersionAsync(SpecVersion version, DateTimeOffset at, CancellationToken cancellationToken) => UpdateVersionAsync(version, at, cancellationToken);

    public Task<long> UpdateVersionAsync(SpecVersion version, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var next = _next++;
            _versions[version.Id] = (version, next);
            return Task.FromResult(next);
        }
    }

    public Task InsertReleasedItemsAsync(SpecVersion version, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var item in version.Definition.Items)
            {
                _released[item.Id] = (item, version.Id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<(SpecItem Item, int VersionNumber)?> GetItemInForceAsync(Guid materialId, Guid characteristicId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var spec = _specs.Values.FirstOrDefault(s => s.Value.MaterialId == materialId && s.Value.IsActive).Value;
            var version = spec is null ? null : _versions.Values.Select(v => v.Value).FirstOrDefault(v => v.SpecId == spec.Id && v.Status == SpecVersionStatus.Released);
            var item = version?.Definition.Items.FirstOrDefault(i => i.CharacteristicId == characteristicId);
            return Task.FromResult<(SpecItem, int)?>(item is null ? null : (item, version!.Number));
        }
    }

    public Task<QualityJobContext?> GetJobContextAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(Jobs.GetValueOrDefault(jobId));

    public Task<LotProduction?> GetLotProductionAsync(Guid lotId, CancellationToken cancellationToken) => Task.FromResult(Productions.GetValueOrDefault(lotId));

    public Task<Guid?> FindReasonAsync(string kind, string code, Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(Reasons.TryGetValue((kind, code), out var id) ? id : null);

    public Task InsertResultAsync(QualityResultRow result, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _results.Add(result);
        }

        return Task.CompletedTask;
    }

    public Task<QualityResultRow?> GetResultForUpdateAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_results.FirstOrDefault(r => r.Id == id && r.OccurredAt == occurredAt));
        }
    }

    public Task ExcludeResultAsync(Guid id, DateTimeOffset occurredAt, Guid reasonId, string? note, string by, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _results.FindIndex(r => r.Id == id && r.OccurredAt == occurredAt);
            _results[index] = _results[index] with { IsExcluded = true };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CharacteristicDto>> ListCharacteristicsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CharacteristicDto>>([.. _characteristics.Values.Select(c => new CharacteristicDto
            {
                Id = c.Value.Id,
                Code = c.Value.Code,
                Name = c.Value.Name,
                DataKind = c.Value.DataKind,
                IsActive = c.Value.IsActive,
                Version = c.Version,
            })]);
        }
    }

    public Task<IReadOnlyList<SpecSummaryDto>> ListSpecsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SpecSummaryDto>>([]);

    public Task<SpecDto?> GetSpecAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<SpecDto?>(null);

    public Task<SpecVersionDto?> GetVersionAsync(Guid specId, Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_versions.TryGetValue(versionId, out var v) && v.Value.SpecId == specId
                ? new SpecVersionDto
                {
                    Id = v.Value.Id,
                    SpecId = specId,
                    Number = v.Value.Number,
                    Status = v.Value.Status,
                    Version = v.Version,
                    Definition = new SpecDefinitionDto
                    {
                        Items = [.. v.Value.Definition.Items.Select(i => new SpecItemDto { Id = i.Id, CharacteristicId = i.CharacteristicId, Lsl = i.Lsl, Usl = i.Usl, MaxDefects = i.MaxDefects })],
                    },
                }
                : null);
        }
    }

    public Task<ReleasedSpecDto?> GetReleasedSpecAsync(Guid materialId, CancellationToken cancellationToken) => Task.FromResult<ReleasedSpecDto?>(null);

    public Task<QualityResultDto?> GetResultAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_results.FirstOrDefault(r => r.Id == id && r.OccurredAt == occurredAt) is { } r
                ? new QualityResultDto
                {
                    Id = r.Id,
                    OccurredAt = r.OccurredAt,
                    SiteId = r.SiteId,
                    CharacteristicId = r.CharacteristicId,
                    CharacteristicCode = _characteristics.TryGetValue(r.CharacteristicId, out var c) ? c.Value.Code : "",
                    CharacteristicName = c.Value?.Name ?? "",
                    DataKind = c.Value?.DataKind ?? "",
                    MaterialId = r.MaterialId,
                    MaterialCode = Materials.GetValueOrDefault(r.MaterialId) ?? "",
                    Conforms = r.Conforms,
                    Source = r.Source,
                    RecordedBy = r.RecordedBy,
                    IsExcluded = r.IsExcluded,
                }
                : null);
        }
    }

    public Task<IReadOnlyList<QualityResultDto>> ListResultsAsync(QualityResultFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<QualityResultDto>>([]);
}
