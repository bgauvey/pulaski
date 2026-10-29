using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Write side for machine profiles and equipment machine data (D-050). Every statement runs in the command's transaction.</summary>
internal sealed class MachineDataRepository(DbSession session) : IMachineDataRepository
{
    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')";

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM core.machine_profile WHERE site_id = @SiteId AND code = @Code)";

    private const string ProfileColumns = "id, site_id, code, name, vendor, model, description, is_active, xmin::text::bigint AS version";

    private const string GetProfile = $"SELECT {ProfileColumns} FROM core.machine_profile WHERE id = @Id FOR UPDATE";

    private const string ShareProfile = $"SELECT {ProfileColumns} FROM core.machine_profile WHERE id = @Id FOR SHARE";

    private const string InsertProfile = """
        INSERT INTO core.machine_profile (id, site_id, code, name, vendor, model, description, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @Vendor, @Model, @Description, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateProfile = """
        UPDATE core.machine_profile SET name = @Name, vendor = @Vendor, model = @Model, description = @Description, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string ActiveMachines = "SELECT code FROM core.equipment WHERE machine_profile_id = @Id AND is_active ORDER BY code";

    private const string States = """
        SELECT profile_id, raw_value, name, packml_state::int AS packml_state, reason_code_id, is_active
        FROM core.machine_profile_state WHERE profile_id = @Id ORDER BY raw_value
        """;

    private const string Codes = """
        SELECT profile_id, code_kind AS kind, raw_value, message, category, reason_code_id, is_active
        FROM core.machine_profile_code WHERE profile_id = @Id ORDER BY code_kind, raw_value
        """;

    private const string Offered = "SELECT reason_code_id FROM core.machine_profile_reason WHERE profile_id = @Id AND is_offered";

    private const string UpsertState = """
        INSERT INTO core.machine_profile_state (profile_id, raw_value, name, packml_state, reason_code_id, is_active)
        VALUES (@ProfileId, @RawValue, @Name, @PackMlState, @ReasonCodeId, @IsActive)
        ON CONFLICT (profile_id, raw_value) DO UPDATE
        SET name = excluded.name, packml_state = excluded.packml_state, reason_code_id = excluded.reason_code_id, is_active = excluded.is_active
        """;

    private const string UpsertCode = """
        INSERT INTO core.machine_profile_code (profile_id, code_kind, raw_value, message, category, reason_code_id, is_active)
        VALUES (@ProfileId, @Kind, @RawValue, @Message, @Category, @ReasonCodeId, @IsActive)
        ON CONFLICT (profile_id, code_kind, raw_value) DO UPDATE
        SET message = excluded.message, category = excluded.category, reason_code_id = excluded.reason_code_id, is_active = excluded.is_active
        """;

    private const string UpsertOffered = """
        INSERT INTO core.machine_profile_reason (profile_id, reason_code_id, is_offered) VALUES (@ProfileId, @ReasonCodeId, @IsOffered)
        ON CONFLICT (profile_id, reason_code_id) DO UPDATE SET is_offered = excluded.is_offered
        """;

    private const string UtilizationReasons = """
        SELECT id, code, utilization_class
        FROM core.reason_code
        WHERE reason_kind = 'utilization' AND is_active AND is_selectable AND (site_id IS NULL OR site_id = @SiteId)
        ORDER BY sort_order, code
        """;

    private const string GetEquipment = """
        SELECT id AS equipment_id, CASE WHEN level = 'site' THEN id ELSE site_id END AS site_id, code, tracks_state, machine_profile_id,
               state_derivation, constraint_equipment_id, micro_stop_threshold, xmin::text::bigint AS version
        FROM core.equipment WHERE id = @Id
        FOR UPDATE
        """;

    private const string Candidate = """
        SELECT e.id AS equipment_id, e.code, e.tracks_state,
               EXISTS (SELECT 1 FROM core.equipment_closure c WHERE c.ancestor_id = @LineId AND c.descendant_id = e.id AND c.depth > 0) AS is_below
        FROM core.equipment e WHERE e.id = @Id
        """;

    private const string SetMachineData = """
        UPDATE core.equipment
        SET machine_profile_id = @MachineProfileId, state_derivation = @StateDerivation, constraint_equipment_id = @ConstraintEquipmentId,
            micro_stop_threshold = @MicroStopThreshold, updated_at = @At
        WHERE id = @EquipmentId
        RETURNING xmin::text::bigint
        """;

    private const string Touch = "UPDATE core.equipment SET updated_at = @At WHERE id = @Id RETURNING xmin::text::bigint";

    private const string UpsertOverride = """
        INSERT INTO core.equipment_stop_reason_map (equipment_id, plc_reason_value, reason_code_id, is_active)
        VALUES (@EquipmentId, @RawValue, @ReasonCodeId, @IsActive)
        ON CONFLICT (equipment_id, plc_reason_value) DO UPDATE SET reason_code_id = excluded.reason_code_id, is_active = excluded.is_active
        """;

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => session.ExecuteScalarAsync<bool>(IsSite, new { Id = siteId }, cancellationToken);

    public Task<bool> ProfileCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public async Task<Versioned<MachineProfileRecord>?> GetProfileForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ProfileRow>(GetProfile, new { Id = id }, cancellationToken) is { } r ? new(r.ToRecord(), r.Version) : null;

    public async Task<MachineProfileRecord?> GetProfileForShareAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ProfileRow>(ShareProfile, new { Id = id }, cancellationToken))?.ToRecord();

    public Task<long> InsertProfileAsync(MachineProfileRecord p, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertProfile, new { p.Id, p.SiteId, p.Code, p.Name, p.Vendor, p.Model, p.Description, p.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateProfileAsync(MachineProfileRecord p, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateProfile, new { p.Id, p.Name, p.Vendor, p.Model, p.Description, p.IsActive, At = at }, cancellationToken);

    public Task<IReadOnlyList<string>> ListActiveMachinesAsync(Guid profileId, CancellationToken cancellationToken) =>
        session.QueryAsync<string>(ActiveMachines, new { Id = profileId }, cancellationToken);

    public async Task<IReadOnlyList<ProfileStateRecord>> ListStatesAsync(Guid profileId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<StateRow>(States, new { Id = profileId }, cancellationToken))
            .Select(r => new ProfileStateRecord(r.ProfileId, r.RawValue, r.Name, r.PackmlState, r.ReasonCodeId, r.IsActive))];

    public async Task<IReadOnlyList<ProfileCodeRecord>> ListCodesAsync(Guid profileId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<CodeRow>(Codes, new { Id = profileId }, cancellationToken))
            .Select(r => new ProfileCodeRecord(r.ProfileId, r.Kind, r.RawValue, r.Message, r.Category, r.ReasonCodeId, r.IsActive))];

    public Task<IReadOnlyList<Guid>> ListOfferedAsync(Guid profileId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(Offered, new { Id = profileId }, cancellationToken);

    public Task UpsertStateAsync(ProfileStateRecord state, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpsertState, new { state.ProfileId, state.RawValue, state.Name, PackMlState = (short)state.PackMlState, state.ReasonCodeId, state.IsActive }, cancellationToken);

    public Task UpsertCodeAsync(ProfileCodeRecord code, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpsertCode, code, cancellationToken);

    public Task UpsertOfferedAsync(Guid profileId, Guid reasonCodeId, bool isOffered, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpsertOffered, new { ProfileId = profileId, ReasonCodeId = reasonCodeId, IsOffered = isOffered }, cancellationToken);

    public async Task<IReadOnlyList<UtilizationReasonRecord>> ListUtilizationReasonsAsync(Guid siteId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<ReasonRow>(UtilizationReasons, new { SiteId = siteId }, cancellationToken)).Select(r => new UtilizationReasonRecord(r.Id, r.Code, r.UtilizationClass))];

    public async Task<Versioned<EquipmentMachineDataRecord>?> GetEquipmentForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<EquipmentRow>(GetEquipment, new { Id = equipmentId }, cancellationToken) is { } r
            ? new(new EquipmentMachineDataRecord(r.EquipmentId, r.SiteId, r.Code, r.TracksState, r.MachineProfileId, r.StateDerivation, r.ConstraintEquipmentId, r.MicroStopThreshold), r.Version)
            : null;

    public async Task<ConstraintCandidate?> GetConstraintCandidateAsync(Guid lineId, Guid candidateId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<CandidateRow>(Candidate, new { LineId = lineId, Id = candidateId }, cancellationToken) is { } r
            ? new ConstraintCandidate(r.EquipmentId, r.Code, r.TracksState, r.IsBelow)
            : null;

    public async Task<long> UpdateEquipmentMachineDataAsync(EquipmentMachineDataRecord e, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await session.ExecuteScalarAsync<long>(SetMachineData,
            new { e.EquipmentId, e.MachineProfileId, e.StateDerivation, e.ConstraintEquipmentId, e.MicroStopThreshold, At = at }, cancellationToken);
        await OeeRollupMarks.SiteOfEquipmentChangedAsync(session, e.EquipmentId, cancellationToken);
        return version;
    }

    public Task<long> TouchEquipmentAsync(Guid equipmentId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Touch, new { Id = equipmentId, At = at }, cancellationToken);

    public Task UpsertOverrideAsync(Guid equipmentId, int rawValue, Guid? reasonCodeId, bool isActive, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpsertOverride, new { EquipmentId = equipmentId, RawValue = rawValue, ReasonCodeId = reasonCodeId, IsActive = isActive }, cancellationToken);

    private sealed class ProfileRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Vendor { get; init; }
        public string? Model { get; init; }
        public string? Description { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public MachineProfileRecord ToRecord() => new(Id, SiteId, Code, Name, Vendor, Model, Description, IsActive);
    }

    private sealed class StateRow
    {
        public Guid ProfileId { get; init; }
        public int RawValue { get; init; }
        public string Name { get; init; } = "";
        public int PackmlState { get; init; }
        public Guid? ReasonCodeId { get; init; }
        public bool IsActive { get; init; }
    }

    private sealed class CodeRow
    {
        public Guid ProfileId { get; init; }
        public string Kind { get; init; } = "";
        public int RawValue { get; init; }
        public string Message { get; init; } = "";
        public string? Category { get; init; }
        public Guid? ReasonCodeId { get; init; }
        public bool IsActive { get; init; }
    }

    private sealed class ReasonRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string UtilizationClass { get; init; } = "";
    }

    private sealed class CandidateRow
    {
        public Guid EquipmentId { get; init; }
        public string Code { get; init; } = "";
        public bool TracksState { get; init; }
        public bool IsBelow { get; init; }
    }

    private sealed class EquipmentRow
    {
        public Guid EquipmentId { get; init; }
        public Guid? SiteId { get; init; }
        public string Code { get; init; } = "";
        public bool TracksState { get; init; }
        public Guid? MachineProfileId { get; init; }
        public string StateDerivation { get; init; } = "";
        public Guid? ConstraintEquipmentId { get; init; }
        public TimeSpan MicroStopThreshold { get; init; }
        public long Version { get; init; }
    }
}
