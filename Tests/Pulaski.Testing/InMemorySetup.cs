using Pulaski.Application.Setup;
using Pulaski.Domain.Setup;

namespace Pulaski.Testing;

/// <summary>The setup checklist in memory (#139): the test sets the counts; marks are kept per site.</summary>
public sealed class InMemorySetup : ISetupReadStore, ISetupRepository
{
    public Dictionary<string, int> Counts { get; } = [];

    public Dictionary<Guid, string> Sites { get; } = [];

    private readonly Dictionary<(Guid, string), SetupSkip> _skips = [];

    public Task<string?> SiteCodeAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(Sites.GetValueOrDefault(siteId));

    public Task<SetupFacts> FactsAsync(Guid? siteId, CancellationToken cancellationToken) => Task.FromResult(new SetupFacts(Counts));

    public Task<IReadOnlyList<SetupSkip>> SkipsAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SetupSkip>>([.. _skips.Where(s => s.Key.Item1 == siteId).Select(s => s.Value)]);

    public Task<bool> LockSiteAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(Sites.ContainsKey(siteId));

    public Task UpsertSkipAsync(Guid siteId, SetupSkip skip, CancellationToken cancellationToken)
    {
        _skips[(siteId, skip.Step)] = skip;
        return Task.CompletedTask;
    }
}
