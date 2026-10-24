using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Testing;

/// <summary>In-memory capabilities, their lines and run standards, for the capability handlers (D-034).</summary>
public sealed class InMemoryCapabilities : ICapabilityRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, CapabilityLine> _lines = [];
    private readonly Dictionary<Guid, (Guid? SiteId, string? SiteCode)> _sites = [];
    private readonly Dictionary<Guid, Versioned<Capability>> _capabilities = [];
    private readonly Dictionary<(Guid, Guid), Versioned<RunStandard>> _standards = [];
    private long _next = 1;

    /// <summary>How many open jobs a test says each line has for each material.</summary>
    public Dictionary<(Guid Line, Guid Material), int> OpenJobs { get; } = [];

    public CapabilityLine AddLine(string code, bool canRunJob = true, bool isActive = true, Guid? siteId = null, string? siteCode = null)
    {
        var line = new CapabilityLine(Guid.CreateVersion7(), code, canRunJob, isActive);
        lock (_lock)
        {
            _lines[line.Id] = line;
            _sites[line.Id] = (siteId, siteCode);
        }

        return line;
    }

    public Versioned<Capability>? Find(Guid id)
    {
        lock (_lock)
        {
            return _capabilities.GetValueOrDefault(id);
        }
    }

    public RunStandard? StandardFor(Guid lineId, Guid materialId)
    {
        lock (_lock)
        {
            return _standards.GetValueOrDefault((lineId, materialId))?.Value;
        }
    }

    public Task<CapabilityLine?> LockLineAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_lines.GetValueOrDefault(equipmentId));
        }
    }

    /// <summary>The capabilities on a line for a material, oldest first.</summary>
    public IReadOnlyList<Capability> On(Guid lineId, Guid materialId)
    {
        lock (_lock)
        {
            return [.. _capabilities.Values.Select(c => c.Value).Where(c => c.EquipmentId == lineId && c.MaterialId == materialId).OrderBy(c => c.EffectiveFrom)];
        }
    }

    public Task<IReadOnlyList<SiteLine>> LockLinesByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<SiteLine>>([.. _lines.Values
                .Where(l => codes.Contains(l.Code.ToLowerInvariant()))
                .Select(l => new SiteLine(l, _sites[l.Id].SiteId, _sites[l.Id].SiteCode))]);
        }
    }

    public Task<Guid?> GetLineOfAsync(Guid capabilityId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(capabilityId)?.Value.EquipmentId);

    public Task<Versioned<Capability>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(id));

    public Task<IReadOnlyList<Capability>> ListOnLineAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Capability>>(
                [.. _capabilities.Values.Select(c => c.Value).Where(c => c.EquipmentId == equipmentId && c.MaterialId == materialId)]);
        }
    }

    public Task<long> InsertAsync(Capability capability, CancellationToken cancellationToken) => UpdateAsync(capability, cancellationToken);

    public Task<long> UpdateAsync(Capability capability, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _capabilities[capability.Id] = new(capability, version);
            return Task.FromResult(version);
        }
    }

    public Task<Versioned<RunStandard>?> GetRunStandardForUpdateAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_standards.GetValueOrDefault((equipmentId, materialId)));
        }
    }

    public Task InsertRunStandardAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpdateRunStandardCalculationAsync(standard, at, cancellationToken);

    public Task UpdateRunStandardCalculationAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _standards[(standard.EquipmentId, standard.MaterialId)] = new(standard, ++_next);
        }

        return Task.CompletedTask;
    }

    /// <summary>Puts a run standard in place, as a test's starting point.</summary>
    public void PutRunStandard(RunStandard standard) => UpdateRunStandardCalculationAsync(standard, DateTimeOffset.UnixEpoch, CancellationToken.None);

    public Task<int> CountOpenJobsAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(OpenJobs.GetValueOrDefault((equipmentId, materialId)));
        }
    }
}
