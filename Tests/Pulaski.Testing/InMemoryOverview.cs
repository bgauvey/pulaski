using Pulaski.Application.Overview;

namespace Pulaski.Testing;

/// <summary>In-memory lines and held lots for the Overview (D-070). Machine history comes from <see cref="InMemoryOee"/>.</summary>
public sealed class InMemoryOverview : IOverviewReadStore
{
    private readonly Lock _lock = new();
    private readonly List<(Guid SiteId, OverviewLineRecord Line)> _lines = [];

    public (int OnHold, int Quarantine) HeldLots { get; set; }

    public void AddLine(Guid siteId, OverviewLineRecord line)
    {
        lock (_lock)
        {
            _lines.Add((siteId, line));
        }
    }

    public Task<IReadOnlyList<OverviewLineRecord>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OverviewLineRecord>>(
                [.. _lines.Where(l => l.SiteId == siteId).Select(l => l.Line).OrderBy(l => l.Code, StringComparer.Ordinal)]);
        }
    }

    public Task<(int OnHold, int Quarantine)> CountHeldLotsAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(HeldLots);
}
