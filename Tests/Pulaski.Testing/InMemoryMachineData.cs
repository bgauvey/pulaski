using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Testing;

/// <summary>In-memory machine profiles and equipment machine data (D-050). Sites, reasons and equipment are added by the test.</summary>
public sealed class InMemoryMachineData : IMachineDataRepository, IMachineDataReadStore
{
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _sites = [];
    private readonly Dictionary<Guid, Versioned<MachineProfileRecord>> _profiles = [];
    private readonly Dictionary<(Guid, int), ProfileStateRecord> _states = [];
    private readonly Dictionary<(Guid, string, int), ProfileCodeRecord> _codes = [];
    private readonly Dictionary<(Guid, Guid), bool> _offered = [];
    private readonly Dictionary<Guid, (Guid? SiteId, UtilizationReasonRecord Reason, bool IsActive)> _reasons = [];
    private readonly Dictionary<Guid, (Versioned<EquipmentMachineDataRecord> Data, Guid? ParentId, bool IsActive)> _equipment = [];
    private readonly Dictionary<(Guid, int), (Guid? ReasonCodeId, bool IsActive)> _overrides = [];
    private long _next = 1;

    public void AddSite(Guid siteId)
    {
        lock (_lock)
        {
            _sites.Add(siteId);
        }
    }

    /// <summary>A utilization reason; with no site it is for every site.</summary>
    public Guid AddReason(Guid? siteId, string code, string utilizationClass = UtilizationClasses.Downtime, bool isActive = true)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _reasons[id] = (siteId, new UtilizationReasonRecord(id, code, utilizationClass), isActive);
        }

        return id;
    }

    public Guid AddEquipment(Guid siteId, string code, bool tracksState = true, Guid? parentId = null)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            var data = new EquipmentMachineDataRecord(id, siteId, code, tracksState, null, StateDerivations.OwnTags, null, MachineProfileRules.DefaultMicroStop);
            _equipment[id] = (new(data, _next++), parentId, true);
        }

        return id;
    }

    public Versioned<MachineProfileRecord> Profile(Guid id)
    {
        lock (_lock)
        {
            return _profiles[id];
        }
    }

    public Versioned<EquipmentMachineDataRecord> Equipment(Guid id)
    {
        lock (_lock)
        {
            return _equipment[id].Data;
        }
    }

    public IReadOnlyList<ProfileStateRecord> States(Guid profileId) => ListStatesAsync(profileId, CancellationToken.None).Result;

    public IReadOnlyList<ProfileCodeRecord> Codes(Guid profileId) => ListCodesAsync(profileId, CancellationToken.None).Result;

    public (Guid? ReasonCodeId, bool IsActive)? Override(Guid equipmentId, int rawValue)
    {
        lock (_lock)
        {
            return _overrides.TryGetValue((equipmentId, rawValue), out var o) ? o : null;
        }
    }

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sites.Contains(siteId));
        }
    }

    public Task<bool> ProfileCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_profiles.Values.Any(p => p.Value.SiteId == siteId && p.Value.Code == code));
        }
    }

    public Task<Versioned<MachineProfileRecord>?> GetProfileForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_profiles.GetValueOrDefault(id));
        }
    }

    public Task<MachineProfileRecord?> GetProfileForShareAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_profiles.GetValueOrDefault(id)?.Value);
        }
    }

    public Task<long> InsertProfileAsync(MachineProfileRecord profile, DateTimeOffset at, CancellationToken cancellationToken) => UpdateProfileAsync(profile, at, cancellationToken);

    public Task<long> UpdateProfileAsync(MachineProfileRecord profile, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _next++;
            _profiles[profile.Id] = new(profile, version);
            return Task.FromResult(version);
        }
    }

    public Task<IReadOnlyList<string>> ListActiveMachinesAsync(Guid profileId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                [.. _equipment.Values.Where(e => e.IsActive && e.Data.Value.MachineProfileId == profileId).Select(e => e.Data.Value.Code).Order(StringComparer.Ordinal)]);
        }
    }

    public Task<IReadOnlyList<ProfileStateRecord>> ListStatesAsync(Guid profileId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ProfileStateRecord>>([.. _states.Values.Where(s => s.ProfileId == profileId).OrderBy(s => s.RawValue)]);
        }
    }

    public Task<IReadOnlyList<ProfileCodeRecord>> ListCodesAsync(Guid profileId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ProfileCodeRecord>>(
                [.. _codes.Values.Where(c => c.ProfileId == profileId).OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.RawValue)]);
        }
    }

    public Task<IReadOnlyList<Guid>> ListOfferedAsync(Guid profileId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([.. _offered.Where(o => o.Key.Item1 == profileId && o.Value).Select(o => o.Key.Item2)]);
        }
    }

    public Task UpsertStateAsync(ProfileStateRecord state, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _states[(state.ProfileId, state.RawValue)] = state;
        }

        return Task.CompletedTask;
    }

    public Task UpsertCodeAsync(ProfileCodeRecord code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _codes[(code.ProfileId, code.Kind, code.RawValue)] = code;
        }

        return Task.CompletedTask;
    }

    public Task UpsertOfferedAsync(Guid profileId, Guid reasonCodeId, bool isOffered, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _offered[(profileId, reasonCodeId)] = isOffered;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UtilizationReasonRecord>> ListUtilizationReasonsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<UtilizationReasonRecord>>(
                [.. _reasons.Values.Where(r => r.IsActive && (r.SiteId is null || r.SiteId == siteId)).Select(r => r.Reason)]);
        }
    }

    public Task<Versioned<EquipmentMachineDataRecord>?> GetEquipmentForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_equipment.TryGetValue(equipmentId, out var e) ? e.Data : null);
        }
    }

    public Task<ConstraintCandidate?> GetConstraintCandidateAsync(Guid lineId, Guid candidateId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_equipment.TryGetValue(candidateId, out var e))
            {
                return Task.FromResult<ConstraintCandidate?>(null);
            }

            var below = false;
            for (var parent = e.ParentId; parent is { } p && !below; parent = _equipment.TryGetValue(p, out var up) ? up.ParentId : null)
            {
                below = p == lineId;
            }

            return Task.FromResult<ConstraintCandidate?>(new ConstraintCandidate(candidateId, e.Data.Value.Code, e.Data.Value.TracksState, below));
        }
    }

    public Task<long> UpdateEquipmentMachineDataAsync(EquipmentMachineDataRecord equipment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _next++;
            var current = _equipment[equipment.EquipmentId];
            _equipment[equipment.EquipmentId] = (new(equipment, version), current.ParentId, current.IsActive);
            return Task.FromResult(version);
        }
    }

    public Task<long> TouchEquipmentAsync(Guid equipmentId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _next++;
            var current = _equipment[equipmentId];
            _equipment[equipmentId] = (current.Data with { Version = version }, current.ParentId, current.IsActive);
            return Task.FromResult(version);
        }
    }

    public Task UpsertOverrideAsync(Guid equipmentId, int rawValue, Guid? reasonCodeId, bool isActive, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _overrides[(equipmentId, rawValue)] = (reasonCodeId, isActive);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MachineProfileDto>> ListProfilesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MachineProfileDto>>(
                [.. _profiles.Values.Where(p => p.Value.SiteId == siteId).OrderBy(p => p.Value.Code, StringComparer.Ordinal).Select(ToDto)]);
        }
    }

    public Task<MachineProfileDetailDto?> GetProfileAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_profiles.TryGetValue(id, out var p) ? new MachineProfileDetailDto { Profile = ToDto(p) } : null);
        }
    }

    private MachineProfileDto ToDto(Versioned<MachineProfileRecord> p) => new()
    {
        Id = p.Value.Id,
        SiteId = p.Value.SiteId,
        Code = p.Value.Code,
        Name = p.Value.Name,
        Vendor = p.Value.Vendor,
        Model = p.Value.Model,
        Description = p.Value.Description,
        IsActive = p.Value.IsActive,
        StopCodeCount = _codes.Values.Count(c => c.ProfileId == p.Value.Id && c.Kind == StopCodeKinds.StopReason && c.IsActive),
        MachineCount = _equipment.Values.Count(e => e.Data.Value.MachineProfileId == p.Value.Id),
        Version = p.Version,
    };
}
