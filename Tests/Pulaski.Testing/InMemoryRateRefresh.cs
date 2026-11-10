using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Testing;

/// <summary>Planning rate refresh (#70): the sites' settings and the run standards' proposals.</summary>
public sealed class InMemoryRateRefresh : IRateRefreshRepository, IRateRefreshReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (RateRefreshSettings Settings, long Version)> _settings = [];
    private readonly Dictionary<Guid, DateTimeOffset> _refreshed = [];

    /// <summary>Every proposal made, with its status as decided.</summary>
    public List<RateProposal> Proposals { get; } = [];

    /// <summary>The run standards of each site.</summary>
    public Dictionary<Guid, List<Guid>> StandardsOfSite { get; } = [];

    public void Set(RateRefreshSettings settings)
    {
        lock (_lock)
        {
            _settings[settings.SiteId] = (settings, _settings.TryGetValue(settings.SiteId, out var s) ? s.Version + 1 : 1);
        }
    }

    public DateTimeOffset? RefreshedAt(Guid siteId)
    {
        lock (_lock)
        {
            return _refreshed.TryGetValue(siteId, out var at) ? at : null;
        }
    }

    public Task<RateRefreshSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_settings.TryGetValue(siteId, out var s) ? s.Settings : RateRefreshSettings.Default(siteId));
        }
    }

    public Task<Versioned<RateRefreshSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_settings.TryGetValue(siteId, out var s)
                ? new Versioned<RateRefreshSettings>(s.Settings, s.Version)
                : new Versioned<RateRefreshSettings>(RateRefreshSettings.Default(siteId), 0));
        }
    }

    public Task<long> UpdateSettingsAsync(RateRefreshSettings settings, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Set(settings);
        lock (_lock)
        {
            return Task.FromResult(_settings[settings.SiteId].Version);
        }
    }

    public Task<IReadOnlyList<(RateRefreshSettings Settings, DateTimeOffset? RefreshedAt)>> ListScheduledAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<(RateRefreshSettings, DateTimeOffset?)>>(
                [.. _settings.Values.Where(s => s.Settings.IsScheduled).Select(s => (s.Settings, _refreshed.TryGetValue(s.Settings.SiteId, out var at) ? at : (DateTimeOffset?)null))]);
        }
    }

    public Task MarkRefreshedAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _refreshed[siteId] = at;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListRunStandardsOfSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(StandardsOfSite.TryGetValue(siteId, out var ids) ? [.. ids] : []);

    public Task<RateProposal?> GetPendingForUpdateAsync(Guid runStandardId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Proposals.FirstOrDefault(p => p.RunStandardId == runStandardId && p.Status == ProposalStatus.Pending));
        }
    }

    public Task InsertProposalAsync(RateProposal proposal, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Proposals.Add(proposal);
        }

        return Task.CompletedTask;
    }

    public Task DecideAsync(Guid proposalId, string status, DateTimeOffset at, string actorId, string? note, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var i = Proposals.FindIndex(p => p.Id == proposalId && p.Status == ProposalStatus.Pending);
            if (i >= 0)
            {
                Proposals[i] = Proposals[i] with { Status = status };
            }
        }

        return Task.CompletedTask;
    }

    public async Task<RateRefreshSettingsDto> GetSettingsDtoAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var s = await GetSettingsAsync(siteId, cancellationToken);
        return new RateRefreshSettingsDto
        {
            SiteId = siteId,
            TakeOutStops = s.TakeOutStops,
            IsScheduled = s.IsScheduled,
            EveryDays = s.EveryDays,
            WindowDays = s.WindowDays,
            ThresholdPct = s.ThresholdPct,
            AutoAccept = s.AutoAccept,
            AutoAcceptLimitPct = s.AutoAcceptLimitPct,
            RefreshedAt = RefreshedAt(siteId),
        };
    }

    public Task<IReadOnlyList<RateProposalDto>> ListProposalsAsync(Guid siteId, DateTimeOffset decidedSince, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var ids = StandardsOfSite.TryGetValue(siteId, out var list) ? list : [];
            return Task.FromResult<IReadOnlyList<RateProposalDto>>([.. Proposals.Where(p => ids.Contains(p.RunStandardId)).Select(ToDto)]);
        }
    }

    public Task<RateProposalDto?> GetPendingAsync(Guid runStandardId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Proposals.Where(p => p.RunStandardId == runStandardId && p.Status == ProposalStatus.Pending).Select(ToDto).FirstOrDefault());
        }
    }

    private static RateProposalDto ToDto(RateProposal p) => new()
    {
        Id = p.Id,
        RunStandardId = p.RunStandardId,
        ProposedRatePerHour = p.ProposedRatePerHour,
        CalculationBasis = p.CalculationBasis,
        WindowDays = p.WindowDays,
        UsedRuns = p.UsedRuns,
        UsedHours = p.UsedHours,
        StoppedHours = p.StoppedHours,
        CalculatedRatePerHour = p.CalculatedRatePerHour,
        ReviewReason = p.ReviewReason,
        Status = p.Status,
        ProposedAt = p.ProposedAt,
    };
}
