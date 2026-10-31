using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Testing;

/// <summary>In-memory changeover rules (D-074). Every site is known; the test names the lines, materials and classes by code.</summary>
public sealed class InMemoryChangeoverRules : IChangeoverRuleRepository, IChangeoverRuleReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (ChangeoverRuleRecord Rule, long Version)> _rules = [];

    public Dictionary<string, Guid> Lines { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Guid> Materials { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Guid> Classes { get; } = new(StringComparer.Ordinal);

    public Task<bool> LockSiteAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<Guid?> SiteOfRuleAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_rules.TryGetValue(id, out var r) ? r.Rule.SiteId : (Guid?)null);
        }
    }

    public Task<IReadOnlyList<Versioned<ChangeoverRuleRecord>>> ListSiteRulesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Versioned<ChangeoverRuleRecord>>>([.. _rules.Values.Where(r => r.Rule.SiteId == siteId).Select(r => new Versioned<ChangeoverRuleRecord>(r.Rule, r.Version))]);
        }
    }

    public Task<IReadOnlyDictionary<string, Guid>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, Guid>>(Lines);

    public Task<IReadOnlyDictionary<string, Guid>> FindMaterialsAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(Materials.Where(m => codes.Contains(m.Key)).ToDictionary());

    public Task<IReadOnlyDictionary<string, Guid>> FindClassesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(Classes.Where(c => codes.Contains(c.Key)).ToDictionary());

    public Task<(IReadOnlySet<Guid> Materials, IReadOnlySet<Guid> Classes)> ExistingAsync(
        IReadOnlyCollection<Guid> materialIds, IReadOnlyCollection<Guid> classIds, CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlySet<Guid>, IReadOnlySet<Guid>)>((Materials.Values.ToHashSet(), Classes.Values.ToHashSet()));

    public Task<long> InsertAsync(ChangeoverRuleRecord rule, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _rules[rule.Id] = (rule, 1);
            return Task.FromResult(1L);
        }
    }

    public Task<long> UpdateAsync(ChangeoverRuleRecord rule, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _rules[rule.Id].Version + 1;
            _rules[rule.Id] = (rule, version);
            return Task.FromResult(version);
        }
    }

    public Task<IReadOnlyList<ChangeoverRuleDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ChangeoverRuleDto>>([.. _rules.Values.Where(r => r.Rule.SiteId == siteId).Select(r => new ChangeoverRuleDto
            {
                Id = r.Rule.Id,
                SiteId = r.Rule.SiteId,
                EquipmentId = r.Rule.Pattern.EquipmentId,
                FromMaterialId = r.Rule.Pattern.FromMaterialId,
                FromMaterialClassId = r.Rule.Pattern.FromMaterialClassId,
                ToMaterialId = r.Rule.Pattern.ToMaterialId,
                ToMaterialClassId = r.Rule.Pattern.ToMaterialClassId,
                DurationMinutes = (int)r.Rule.Duration.TotalMinutes,
                Note = r.Rule.Note,
                IsActive = r.Rule.IsActive,
                Version = r.Version,
            })]);
        }
    }
}
