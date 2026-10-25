using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Materials;

/// <summary>Write side for capabilities and the run standards they seed (D-020, D-034). Every statement runs in the command's transaction.</summary>
internal sealed class CapabilityRepository(DbSession session) : ICapabilityRepository
{
    private const string LockLine = "SELECT id, code, can_run_job, is_active FROM core.equipment WHERE id = @Id FOR UPDATE";

    private const string LockLinesByCode = """
        SELECT e.id, e.code, e.can_run_job, e.is_active, e.site_id, s.code AS site_code
        FROM core.equipment e
        LEFT JOIN core.equipment s ON s.id = e.site_id
        WHERE lower(e.code) = ANY(@Codes)
        ORDER BY e.id
        FOR UPDATE OF e
        """;

    private const string LineOf = "SELECT equipment_id FROM core.equipment_capability WHERE id = @Id";

    private const string Columns = """
        id, equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, standard_changeover, standard_crew_size,
        is_preferred, effective_from, effective_to, batch_size
        """;

    private const string GetForUpdate = $"SELECT {Columns}, xmin::text::bigint AS version FROM core.equipment_capability WHERE id = @Id FOR UPDATE";

    private const string ListOnLine = $"""
        SELECT {Columns}, xmin::text::bigint AS version
        FROM core.equipment_capability
        WHERE equipment_id = @EquipmentId AND material_id = @MaterialId
        ORDER BY effective_from
        """;

    private const string Insert = """
        INSERT INTO core.equipment_capability (id, equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, standard_changeover,
                                               standard_crew_size, is_preferred, effective_from, effective_to, batch_size)
        VALUES (@Id, @EquipmentId, @MaterialId, @IdealRatePerHour, @RateUomId, @StandardChangeover,
                @StandardCrewSize, @IsPreferred, @EffectiveFrom, @EffectiveTo, @BatchSize)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.equipment_capability
        SET ideal_rate_per_hour = @IdealRatePerHour, rate_uom_id = @RateUomId, standard_changeover = @StandardChangeover,
            standard_crew_size = @StandardCrewSize, is_preferred = @IsPreferred, effective_from = @EffectiveFrom, effective_to = @EffectiveTo,
            batch_size = @BatchSize
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string GetRunStandardForUpdate = """
        SELECT id, equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at,
               modified_rate_per_hour, modified_at, modified_by, modified_note, xmin::text::bigint AS version
        FROM core.run_standard
        WHERE equipment_id = @EquipmentId AND material_id = @MaterialId
        FOR UPDATE
        """;

    private const string InsertRunStandard = """
        INSERT INTO core.run_standard (id, equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at,
                                       created_at, updated_at)
        VALUES (@Id, @EquipmentId, @MaterialId, @UomId, @CalculatedRatePerHour, @CalculationBasis, @CalculatedAt, @At, @At)
        """;

    private const string UpdateRunStandardCalculation = """
        UPDATE core.run_standard
        SET uom_id = @UomId, calculated_rate_per_hour = @CalculatedRatePerHour, calculation_basis = @CalculationBasis,
            calculated_at = @CalculatedAt, updated_at = @At
        WHERE id = @Id
        """;

    private const string CountOpenJobs = """
        SELECT count(*)::int FROM core.job
        WHERE equipment_id = @EquipmentId AND material_id = @MaterialId AND job_type = 'production'
          AND status NOT IN ('completed','closed','cancelled')
        """;

    public async Task<CapabilityLine?> LockLineAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LineRow>(LockLine, new { Id = equipmentId }, cancellationToken) is { } r
            ? new CapabilityLine(r.Id, r.Code, r.CanRunJob, r.IsActive)
            : null;

    public async Task<IReadOnlyList<SiteLine>> LockLinesByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LineRow>(LockLinesByCode, new { Codes = codes.ToArray() }, cancellationToken))
            .Select(r => new SiteLine(new CapabilityLine(r.Id, r.Code, r.CanRunJob, r.IsActive), r.SiteId, r.SiteCode))];

    public Task<Guid?> GetLineOfAsync(Guid capabilityId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(LineOf, new { Id = capabilityId }, cancellationToken);

    public async Task<Versioned<Capability>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<CapabilityRow>(GetForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new(r.ToDomain(), r.Version)
            : null;

    public async Task<IReadOnlyList<Capability>> ListOnLineAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<CapabilityRow>(ListOnLine, new { EquipmentId = equipmentId, MaterialId = materialId }, cancellationToken))
            .Select(r => r.ToDomain())];

    public Task<long> InsertAsync(Capability capability, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, capability, cancellationToken);

    public Task<long> UpdateAsync(Capability capability, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, capability, cancellationToken);

    public async Task<Versioned<RunStandard>?> GetRunStandardForUpdateAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<RunStandardRow>(
            GetRunStandardForUpdate, new { EquipmentId = equipmentId, MaterialId = materialId }, cancellationToken) is { } r
            ? new(r.ToDomain(), r.Version)
            : null;

    public Task InsertRunStandardAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertRunStandard, Args(standard, at), cancellationToken);

    public Task UpdateRunStandardCalculationAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpdateRunStandardCalculation, Args(standard, at), cancellationToken);

    public Task<int> CountOpenJobsAsync(Guid equipmentId, Guid materialId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<int>(CountOpenJobs, new { EquipmentId = equipmentId, MaterialId = materialId }, cancellationToken);

    private static object Args(RunStandard s, DateTimeOffset at) => new
    {
        s.Id,
        s.EquipmentId,
        s.MaterialId,
        s.UomId,
        s.CalculatedRatePerHour,
        s.CalculationBasis,
        s.CalculatedAt,
        At = at,
    };

    private sealed class LineRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public bool CanRunJob { get; init; }
        public bool IsActive { get; init; }
        public Guid? SiteId { get; init; }
        public string? SiteCode { get; init; }
    }

    private sealed class CapabilityRow
    {
        public Guid Id { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid MaterialId { get; init; }
        public decimal IdealRatePerHour { get; init; }
        public Guid RateUomId { get; init; }
        public TimeSpan? StandardChangeover { get; init; }
        public decimal? StandardCrewSize { get; init; }
        public bool IsPreferred { get; init; }
        public DateTimeOffset EffectiveFrom { get; init; }
        public DateTimeOffset? EffectiveTo { get; init; }
        public decimal? BatchSize { get; init; }
        public long Version { get; init; }

        public Capability ToDomain() => new(
            Id, EquipmentId, MaterialId, IdealRatePerHour, RateUomId, StandardChangeover, StandardCrewSize, IsPreferred, EffectiveFrom, EffectiveTo, BatchSize);
    }

    private sealed class RunStandardRow
    {
        public Guid Id { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid MaterialId { get; init; }
        public Guid UomId { get; init; }
        public decimal CalculatedRatePerHour { get; init; }
        public string CalculationBasis { get; init; } = "";
        public DateTimeOffset CalculatedAt { get; init; }
        public decimal? ModifiedRatePerHour { get; init; }
        public DateTimeOffset? ModifiedAt { get; init; }
        public string? ModifiedBy { get; init; }
        public string? ModifiedNote { get; init; }
        public long Version { get; init; }

        public RunStandard ToDomain() => new(
            Id, EquipmentId, MaterialId, UomId, CalculatedRatePerHour, CalculationBasis, CalculatedAt,
            ModifiedRatePerHour, ModifiedAt, ModifiedBy, ModifiedNote);
    }
}
