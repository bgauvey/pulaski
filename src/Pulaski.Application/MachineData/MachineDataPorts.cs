using Pulaski.Application.Abstractions;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Application.MachineData;

public sealed record MachineProfileRecord(Guid Id, Guid SiteId, string Code, string Name, string? Vendor, string? Model, string? Description, bool IsActive);

/// <summary>A value of a profile's state tag and the PackML state it means (D-050).</summary>
public sealed record ProfileStateRecord(Guid ProfileId, int RawValue, string Name, int PackMlState, Guid? ReasonCodeId, bool IsActive);

/// <summary>A stop, alarm or warning code of a profile and its utilization reason (D-050).</summary>
public sealed record ProfileCodeRecord(Guid ProfileId, string Kind, int RawValue, string Message, string? Category, Guid? ReasonCodeId, bool IsActive);

/// <summary>A utilization reason a profile at a site may use: an active one for every site or for that site.</summary>
public sealed record UtilizationReasonRecord(Guid Id, string Code, string UtilizationClass);

/// <summary>The machine data on equipment (D-050).</summary>
public sealed record EquipmentMachineDataRecord(
    Guid EquipmentId, Guid? SiteId, string Code, bool TracksState, Guid? MachineProfileId, string StateDerivation, Guid? ConstraintEquipmentId, TimeSpan MicroStopThreshold);

/// <summary>Equipment as the constraint machine of another: where it sits and whether it tracks state.</summary>
public sealed record ConstraintCandidate(Guid EquipmentId, string Code, bool TracksState, bool IsBelow);

/// <summary>Write side for machine profiles and equipment machine data (D-050): runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IMachineDataRepository
{
    Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<bool> ProfileCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<Versioned<MachineProfileRecord>?> GetProfileForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The profile, locked so it can't change while equipment takes it on.</summary>
    Task<MachineProfileRecord?> GetProfileForShareAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertProfileAsync(MachineProfileRecord profile, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the profile and bumps its version; called for every change to its lists too.</summary>
    Task<long> UpdateProfileAsync(MachineProfileRecord profile, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Codes of active equipment that use the profile.</summary>
    Task<IReadOnlyList<string>> ListActiveMachinesAsync(Guid profileId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProfileStateRecord>> ListStatesAsync(Guid profileId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProfileCodeRecord>> ListCodesAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Ids of the reasons ticked as offered.</summary>
    Task<IReadOnlyList<Guid>> ListOfferedAsync(Guid profileId, CancellationToken cancellationToken);

    Task UpsertStateAsync(ProfileStateRecord state, CancellationToken cancellationToken);

    Task UpsertCodeAsync(ProfileCodeRecord code, CancellationToken cancellationToken);

    Task UpsertOfferedAsync(Guid profileId, Guid reasonCodeId, bool isOffered, CancellationToken cancellationToken);

    /// <summary>The active utilization reasons for every site or for this one.</summary>
    Task<IReadOnlyList<UtilizationReasonRecord>> ListUtilizationReasonsAsync(Guid siteId, CancellationToken cancellationToken);

    Task<Versioned<EquipmentMachineDataRecord>?> GetEquipmentForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task<ConstraintCandidate?> GetConstraintCandidateAsync(Guid lineId, Guid candidateId, CancellationToken cancellationToken);

    Task<long> UpdateEquipmentMachineDataAsync(EquipmentMachineDataRecord equipment, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Bumps the equipment's version, after a change to its overrides.</summary>
    Task<long> TouchEquipmentAsync(Guid equipmentId, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpsertOverrideAsync(Guid equipmentId, int rawValue, Guid? reasonCodeId, bool isActive, CancellationToken cancellationToken);
}

/// <summary>Read side for machine profiles (D-050).</summary>
public interface IMachineDataReadStore
{
    Task<IReadOnlyList<MachineProfileDto>> ListProfilesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<MachineProfileDetailDto?> GetProfileAsync(Guid id, CancellationToken cancellationToken);
}
