using Dapper;
using Npgsql;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Read side for machine profiles (D-050).</summary>
internal sealed class MachineDataQueries(NpgsqlDataSource dataSource) : IMachineDataReadStore
{
    private const string Profiles = """
        SELECT p.id, p.site_id, p.code, p.name, p.vendor, p.model, p.description, p.is_active,
               (SELECT count(*)::int FROM core.machine_profile_code c WHERE c.profile_id = p.id AND c.code_kind = 'stop_reason' AND c.is_active) AS stop_code_count,
               (SELECT count(*)::int FROM core.machine_profile_code c
                WHERE c.profile_id = p.id AND c.code_kind = 'stop_reason' AND c.is_active AND c.reason_code_id IS NULL) AS unmapped_stop_code_count,
               (SELECT count(*)::int FROM core.equipment e WHERE e.machine_profile_id = p.id) AS machine_count,
               p.xmin::text::bigint AS version
        FROM core.machine_profile p
        """;

    private const string ListSql = $"{Profiles} WHERE p.site_id = @SiteId ORDER BY p.code";

    private const string GetSql = $"{Profiles} WHERE p.id = @Id";

    private const string StatesSql = """
        SELECT s.raw_value, s.name, s.packml_state::int AS pack_ml_state, s.reason_code_id, r.code AS reason_code, s.is_active
        FROM core.machine_profile_state s
        LEFT JOIN core.reason_code r ON r.id = s.reason_code_id
        WHERE s.profile_id = @Id
        ORDER BY s.raw_value
        """;

    private const string CodesSql = """
        SELECT c.code_kind AS kind, c.raw_value, c.message, c.category, c.reason_code_id, r.code AS reason_code, c.is_active
        FROM core.machine_profile_code c
        LEFT JOIN core.reason_code r ON r.id = c.reason_code_id
        WHERE c.profile_id = @Id
        ORDER BY CASE c.code_kind WHEN 'stop_reason' THEN 0 WHEN 'alarm' THEN 1 ELSE 2 END, c.raw_value
        """;

    private const string OfferedSql = "SELECT reason_code_id FROM core.machine_profile_reason WHERE profile_id = @Id AND is_offered";

    private const string MachinesSql = """
        SELECT e.id AS equipment_id, e.code, e.name, e.is_active, e.xmin::text::bigint AS version
        FROM core.equipment e WHERE e.machine_profile_id = @Id ORDER BY e.code
        """;

    private const string OverridesSql = """
        SELECT m.equipment_id, m.plc_reason_value AS raw_value, m.reason_code_id, r.code AS reason_code
        FROM core.equipment_stop_reason_map m
        JOIN core.equipment e ON e.id = m.equipment_id
        LEFT JOIN core.reason_code r ON r.id = m.reason_code_id
        WHERE e.machine_profile_id = @Id AND m.is_active
        ORDER BY m.plc_reason_value
        """;

    public async Task<IReadOnlyList<MachineProfileDto>> ListProfilesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<MachineProfileDto>(new CommandDefinition(ListSql, new { SiteId = siteId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<MachineProfileDetailDto?> GetProfileAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = id };
        var profile = await connection.QuerySingleOrDefaultAsync<MachineProfileDto>(new CommandDefinition(GetSql, args, cancellationToken: cancellationToken));
        if (profile is null)
        {
            return null;
        }

        var states = await connection.QueryAsync<StateRow>(new CommandDefinition(StatesSql, args, cancellationToken: cancellationToken));
        var codes = await connection.QueryAsync<ProfileCodeDto>(new CommandDefinition(CodesSql, args, cancellationToken: cancellationToken));
        var offered = await connection.QueryAsync<Guid>(new CommandDefinition(OfferedSql, args, cancellationToken: cancellationToken));
        var machines = await connection.QueryAsync<MachineRow>(new CommandDefinition(MachinesSql, args, cancellationToken: cancellationToken));
        var overrides = (await connection.QueryAsync<OverrideRow>(new CommandDefinition(OverridesSql, args, cancellationToken: cancellationToken)))
            .ToLookup(o => o.EquipmentId);

        return new MachineProfileDetailDto
        {
            Profile = profile,
            States = [.. states.Select(s => new ProfileStateDto
            {
                RawValue = s.RawValue,
                Name = s.Name,
                PackMlState = s.PackMlState,
                PackMlName = PackMlStates.Name(s.PackMlState),
                ReasonCodeId = s.ReasonCodeId,
                ReasonCode = s.ReasonCode,
                IsActive = s.IsActive,
            })],
            Codes = codes.AsList(),
            OfferedReasonIds = offered.AsList(),
            Machines = [.. machines.Select(m => new ProfileMachineDto
            {
                EquipmentId = m.EquipmentId,
                Code = m.Code,
                Name = m.Name,
                IsActive = m.IsActive,
                Version = m.Version,
                Overrides = [.. overrides[m.EquipmentId].Select(o => new StopReasonOverrideDto { RawValue = o.RawValue, ReasonCodeId = o.ReasonCodeId, ReasonCode = o.ReasonCode })],
            })],
        };
    }

    private sealed class StateRow
    {
        public int RawValue { get; init; }
        public string Name { get; init; } = "";
        public int PackMlState { get; init; }
        public Guid? ReasonCodeId { get; init; }
        public string? ReasonCode { get; init; }
        public bool IsActive { get; init; }
    }

    private sealed class MachineRow
    {
        public Guid EquipmentId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class OverrideRow
    {
        public Guid EquipmentId { get; init; }
        public int RawValue { get; init; }
        public Guid? ReasonCodeId { get; init; }
        public string? ReasonCode { get; init; }
    }
}
