using Pulaski.Application.Abstractions;
using Pulaski.Application.Quality;
using Pulaski.Domain.Quality;
using Pulaski.Infrastructure.Inventory;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.Processes;

namespace Pulaski.Infrastructure.Quality;

/// <summary>SQL shared by the write and read sides of quality (#65, D-085).</summary>
internal static class QualitySql
{
    public const string CharacteristicColumns = """
        c.id, c.code, c.name, c.description, c.data_kind, c.uom_id, c.decimals, c.is_active, c.chart_type, c.subgroup_size::int AS subgroup_size, c.run_rules::int[] AS run_rules,
        c.xmin::text::bigint AS version
        """;

    public const string Characteristics = $"SELECT {CharacteristicColumns} FROM core.quality_characteristic c WHERE c.id = ANY(@Ids)";

    public const string VersionColumns = """
        v.id, v.spec_id, v.version_number AS number, v.status, v.definition::text AS definition_json, v.released_at, v.released_by, v.note,
        v.xmin::text::bigint AS version
        """;

    public const string LimitColumns = """
        l.id, l.characteristic_id, l.material_id, l.equipment_id, l.chart_type, l.subgroup_size::int AS subgroup_size, l.center_line, l.ucl, l.lcl,
        l.dispersion_center, l.dispersion_ucl, l.dispersion_lcl, l.method, l.baseline_from, l.baseline_to, l.baseline_subgroups, l.left_out_sample_ids,
        l.note, l.effective_from, l.effective_to, l.approved_by, l.approved_at, l.ended_by
        """;

    // Results for a baseline: the characteristic's, for the material, not excluded, on the line when one is given.
    public const string Baseline = """
        SELECT q.id AS sample_id, q.occurred_at, q.measured_values, q.inspected_count, q.defect_count
        FROM ts.quality_sample q
        WHERE q.characteristic_id = @CharacteristicId AND q.material_id = @MaterialId AND NOT q.is_excluded
          AND q.occurred_at >= @From AND q.occurred_at < @To
          AND (@EquipmentId::uuid IS NULL OR q.equipment_id = @EquipmentId)
        ORDER BY q.occurred_at, q.id
        LIMIT @Limit
        """;

    internal sealed class ChartSampleRow
    {
        public Guid SampleId { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public decimal[]? MeasuredValues { get; init; }
        public int? InspectedCount { get; init; }
        public int? DefectCount { get; init; }
        public Guid? EquipmentId { get; init; }
        public string? LineCode { get; init; }
        public string? LotLabel { get; init; }
        public string? JobNumber { get; init; }

        public ChartSample ToSample() => new(
            new(SampleId, OccurredAt, MeasuredValues is { } v ? [.. v.Select(x => (double)x)] : null, InspectedCount, DefectCount),
            EquipmentId, LineCode, LotLabel, JobNumber);
    }

    internal sealed class BaselineRow
    {
        public Guid SampleId { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public decimal[]? MeasuredValues { get; init; }
        public int? InspectedCount { get; init; }
        public int? DefectCount { get; init; }

        public Domain.Quality.Spc.SpcSubgroup ToSubgroup() =>
            new(SampleId, OccurredAt, MeasuredValues is { } v ? [.. v.Select(x => (double)x)] : null, InspectedCount, DefectCount);
    }

    internal class LimitRow
    {
        public Guid Id { get; init; }
        public Guid CharacteristicId { get; init; }
        public Guid MaterialId { get; init; }
        public Guid? EquipmentId { get; init; }
        public string ChartType { get; init; } = "";
        public int SubgroupSize { get; init; }
        public double CenterLine { get; init; }
        public double Ucl { get; init; }
        public double Lcl { get; init; }
        public double? DispersionCenter { get; init; }
        public double? DispersionUcl { get; init; }
        public double? DispersionLcl { get; init; }
        public string Method { get; init; } = "";
        public DateTimeOffset? BaselineFrom { get; init; }
        public DateTimeOffset? BaselineTo { get; init; }
        public int? BaselineSubgroups { get; init; }
        public Guid[] LeftOutSampleIds { get; init; } = [];
        public string? Note { get; init; }
        public DateTimeOffset EffectiveFrom { get; init; }
        public DateTimeOffset? EffectiveTo { get; init; }
        public string ApprovedBy { get; init; } = "";
        public DateTimeOffset ApprovedAt { get; init; }
        public string? EndedBy { get; init; }

        public Domain.Quality.Spc.ControlLimit ToDomain() => new(
            Id, CharacteristicId, MaterialId, EquipmentId, ChartType, SubgroupSize,
            new(CenterLine, Ucl, Lcl, DispersionCenter, DispersionUcl, DispersionLcl), Method,
            BaselineFrom is { } from && BaselineTo is { } to ? new(from, to, BaselineSubgroups ?? 0, LeftOutSampleIds) : null,
            Note, EffectiveFrom, EffectiveTo, ApprovedBy, ApprovedAt);
    }

    internal sealed class CharacteristicRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string DataKind { get; init; } = "";
        public Guid? UomId { get; init; }
        public int Decimals { get; init; }
        public bool IsActive { get; init; }
        public string ChartType { get; init; } = "";
        public int SubgroupSize { get; init; } = 1;
        public int[] RunRules { get; init; } = [];
        public long Version { get; init; }

        public Characteristic ToDomain() => new(Id, Code, Name, Description, DataKind, UomId, Decimals, IsActive)
        {
            ChartType = ChartType,
            SubgroupSize = SubgroupSize,
            RunRules = RunRules,
        };
    }

    internal sealed class VersionRow
    {
        public Guid Id { get; init; }
        public Guid SpecId { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public string DefinitionJson { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? ReleasedBy { get; init; }
        public string? Note { get; init; }
        public long Version { get; init; }

        public Versioned<SpecVersion> ToVersioned() => new(
            new SpecVersion(Id, SpecId, Number, Status, new SpecDefinition(ProcessJson.Read<SpecItemsDocument>(DefinitionJson).Items), ReleasedAt, ReleasedBy, Note),
            Version);
    }
}

/// <summary>The stored shape of <c>core.material_spec_version.definition</c>: the items, camelCase, the same as the API's item DTOs.</summary>
internal sealed record SpecItemsDocument(IReadOnlyList<SpecItem> Items);

/// <summary>Write side of quality (#65, D-085). Every statement runs in the command's transaction.</summary>
internal sealed class QualityRepository(DbSession session) : IQualityRepository
{
    private const string CharacteristicForUpdate = $"SELECT {QualitySql.CharacteristicColumns} FROM core.quality_characteristic c WHERE c.id = @Id FOR UPDATE";

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM core.quality_characteristic WHERE site_id IS NULL AND code = @Code)";

    private const string InsertCharacteristic = """
        INSERT INTO core.quality_characteristic (id, site_id, code, name, description, data_kind, chart_type, subgroup_size, uom_id, decimals, is_active,
                                                 run_rules, created_at, updated_at)
        VALUES (@Id, NULL, @Code, @Name, @Description, @DataKind, @ChartType, @SubgroupSize, @UomId, @Decimals, @IsActive, @RunRules, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateCharacteristic = """
        UPDATE core.quality_characteristic
        SET name = @Name, description = @Description, uom_id = @UomId, decimals = @Decimals, is_active = @IsActive, chart_type = @ChartType,
            subgroup_size = @SubgroupSize, run_rules = @RunRules, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string Material = "SELECT code, is_active FROM core.material WHERE id = @Id";

    private const string SpecExists = "SELECT EXISTS (SELECT 1 FROM core.material_spec WHERE material_id = @MaterialId)";

    private const string SpecForUpdate = "SELECT id, material_id, is_active, xmin::text::bigint AS version FROM core.material_spec WHERE id = @Id FOR UPDATE";

    private const string InsertSpec = """
        INSERT INTO core.material_spec (id, material_id, is_active, created_at, updated_at) VALUES (@Id, @MaterialId, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string VersionForUpdate = $"""
        SELECT {QualitySql.VersionColumns} FROM core.material_spec_version v WHERE v.spec_id = @SpecId AND v.id = @VersionId FOR UPDATE
        """;

    private const string ReleasedForUpdate = $"""
        SELECT {QualitySql.VersionColumns} FROM core.material_spec_version v WHERE v.spec_id = @SpecId AND v.status = 'released' FOR UPDATE
        """;

    private const string HasDraft = "SELECT EXISTS (SELECT 1 FROM core.material_spec_version WHERE spec_id = @SpecId AND status = 'draft')";

    private const string InsertVersion = """
        INSERT INTO core.material_spec_version (id, spec_id, version_number, status, definition, released_at, released_by, note, created_at, updated_at)
        VALUES (@Id, @SpecId, @Number, @Status, @Definition::jsonb, @ReleasedAt, @ReleasedBy, @Note, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateVersion = """
        UPDATE core.material_spec_version
        SET status = @Status, definition = @Definition::jsonb, released_at = @ReleasedAt, released_by = @ReleasedBy, note = @Note, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string InsertItem = """
        INSERT INTO core.material_spec_item (id, spec_version_id, characteristic_id, lsl, target, usl, max_defects, hold_on_fail, sort_order)
        VALUES (@Id, @VersionId, @CharacteristicId, @Lsl, @Target, @Usl, @MaxDefects, @HoldOnFail, @SortOrder)
        """;

    private const string ItemInForce = """
        SELECT i.id, i.characteristic_id, i.lsl, i.target, i.usl, i.max_defects, i.hold_on_fail, i.sort_order, v.version_number
        FROM core.material_spec s
        JOIN core.material_spec_version v ON v.spec_id = s.id AND v.status = 'released'
        JOIN core.material_spec_item i ON i.spec_version_id = v.id
        WHERE s.material_id = @MaterialId AND s.is_active AND i.characteristic_id = @CharacteristicId
        FOR SHARE OF v
        """;

    // The run the job had at the time: the one open then, the latest such.
    private const string JobContext = """
        SELECT j.id AS job_id, j.job_number, j.site_id, j.material_id, j.equipment_id, r.id AS run_id, r.equipment_id AS run_equipment_id
        FROM core.job j
        LEFT JOIN LATERAL (
            SELECT r.id, r.equipment_id FROM core.job_run r
            WHERE r.job_id = j.id AND r.started_at <= @At AND (r.ended_at IS NULL OR r.ended_at >= @At)
            ORDER BY r.started_at DESC LIMIT 1) r ON true
        WHERE j.id = @JobId
        """;

    private const string LotProduction = $"""
        SELECT e.job_id, e.run_id, e.effective_at
        FROM ts.lot_event e
        WHERE e.lot_id = @LotId AND e.event_kind = 'produced' AND e.job_id IS NOT NULL AND {LotEventSql.NotReversed}
        ORDER BY e.occurred_at
        LIMIT 1
        """;

    private const string FindReason = """
        SELECT id FROM core.reason_code
        WHERE reason_kind = @Kind AND code = @Code AND is_active AND (site_id = @SiteId OR site_id IS NULL)
        ORDER BY site_id NULLS LAST
        LIMIT 1
        """;

    private const string InsertResult = """
        INSERT INTO ts.quality_sample (id, occurred_at, site_id, characteristic_id, equipment_id, job_id, run_id, lot_id, material_id, subgroup_size,
                                       measured_values, mean, range, std_dev, inspected_count, defect_count, spec_item_id, conforms, source,
                                       entered_by_user_id, recorded_by, recorded_at, comment)
        VALUES (@Id, @OccurredAt, @SiteId, @CharacteristicId, @EquipmentId, @JobId, @RunId, @LotId, @MaterialId, @SubgroupSize,
                @Values, @Mean, @Range, @StdDev, @Inspected, @Defects, @SpecItemId, @Conforms, @Source,
                @EnteredByUserId, @RecordedBy, @RecordedAt, @Comment)
        """;

    private const string ResultForUpdate = """
        SELECT id, occurred_at, site_id, characteristic_id, equipment_id, job_id, run_id, lot_id, material_id, measured_values, inspected_count,
               defect_count, mean, range, std_dev, spec_item_id, conforms, source, entered_by_user_id, recorded_by, recorded_at, comment, is_excluded
        FROM ts.quality_sample
        WHERE id = @Id AND occurred_at = @OccurredAt
        FOR UPDATE
        """;

    private const string Exclude = """
        UPDATE ts.quality_sample
        SET is_excluded = true, exclusion_reason_id = @ReasonId, excluded_note = @Note, excluded_by = @By, excluded_at = @At
        WHERE id = @Id AND occurred_at = @OccurredAt
        """;

    public async Task<Versioned<Characteristic>?> GetCharacteristicForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<QualitySql.CharacteristicRow>(CharacteristicForUpdate, new { Id = id }, cancellationToken) is { } row
            ? new Versioned<Characteristic>(row.ToDomain(), row.Version)
            : null;

    public async Task<IReadOnlyList<Characteristic>> GetCharacteristicsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<QualitySql.CharacteristicRow>(QualitySql.Characteristics, new { Ids = ids.ToArray() }, cancellationToken)).Select(r => r.ToDomain())];

    public Task<bool> CharacteristicCodeExistsAsync(string code, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<bool>(CodeExists, new { Code = code }, cancellationToken);

    public Task<long> InsertCharacteristicAsync(Characteristic characteristic, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertCharacteristic, CharacteristicArgs(characteristic, at), cancellationToken);

    public Task<long> UpdateCharacteristicAsync(Characteristic characteristic, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateCharacteristic, CharacteristicArgs(characteristic, at), cancellationToken);

    public async Task<(string Code, bool IsActive)?> GetMaterialAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<MaterialRow>(Material, new { Id = id }, cancellationToken) is { } row ? (row.Code, row.IsActive) : null;

    public Task<bool> SpecExistsForMaterialAsync(Guid materialId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<bool>(SpecExists, new { MaterialId = materialId }, cancellationToken);

    public async Task<Versioned<MaterialSpec>?> GetSpecForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<SpecRow>(SpecForUpdate, new { Id = id }, cancellationToken) is { } row
            ? new Versioned<MaterialSpec>(new MaterialSpec(row.Id, row.MaterialId, row.IsActive), row.Version)
            : null;

    public Task<long> InsertSpecAsync(MaterialSpec spec, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertSpec, new { spec.Id, spec.MaterialId, spec.IsActive, At = at }, cancellationToken);

    public async Task<Versioned<SpecVersion>?> GetVersionForUpdateAsync(Guid specId, Guid versionId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<QualitySql.VersionRow>(VersionForUpdate, new { SpecId = specId, VersionId = versionId }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<SpecVersion>?> GetReleasedForUpdateAsync(Guid specId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<QualitySql.VersionRow>(ReleasedForUpdate, new { SpecId = specId }, cancellationToken))?.ToVersioned();

    public Task<bool> HasDraftAsync(Guid specId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<bool>(HasDraft, new { SpecId = specId }, cancellationToken);

    public Task<long> InsertVersionAsync(SpecVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertVersion, VersionArgs(version, at), cancellationToken);

    public Task<long> UpdateVersionAsync(SpecVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateVersion, VersionArgs(version, at), cancellationToken);

    public async Task InsertReleasedItemsAsync(SpecVersion version, CancellationToken cancellationToken)
    {
        foreach (var item in version.Definition.Items)
        {
            await session.ExecuteAsync(
                InsertItem,
                new { item.Id, VersionId = version.Id, item.CharacteristicId, item.Lsl, item.Target, item.Usl, item.MaxDefects, item.HoldOnFail, item.SortOrder },
                cancellationToken);
        }
    }

    public async Task<(SpecItem Item, int VersionNumber)?> GetItemInForceAsync(Guid materialId, Guid characteristicId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ItemRow>(ItemInForce, new { MaterialId = materialId, CharacteristicId = characteristicId }, cancellationToken) is { } row
            ? (new SpecItem(row.Id, row.CharacteristicId, row.Lsl, row.Target, row.Usl, row.MaxDefects, row.HoldOnFail, row.SortOrder), row.VersionNumber)
            : null;

    public async Task<QualityJobContext?> GetJobContextAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<JobRow>(JobContext, new { JobId = jobId, At = at }, cancellationToken) is { } row
            ? new QualityJobContext(row.JobId, row.JobNumber, row.SiteId, row.MaterialId, row.EquipmentId, row.RunId, row.RunEquipmentId)
            : null;

    public async Task<LotProduction?> GetLotProductionAsync(Guid lotId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ProductionRow>(LotProduction, new { LotId = lotId }, cancellationToken) is { } row
            ? new LotProduction(row.JobId, row.RunId, row.EffectiveAt)
            : null;

    public Task<Guid?> FindReasonAsync(string kind, string code, Guid siteId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(FindReason, new { Kind = kind, Code = code, SiteId = siteId }, cancellationToken);

    public Task InsertResultAsync(QualityResultRow result, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            InsertResult,
            new
            {
                result.Id,
                result.OccurredAt,
                result.SiteId,
                result.CharacteristicId,
                result.EquipmentId,
                result.JobId,
                result.RunId,
                result.LotId,
                result.MaterialId,
                SubgroupSize = (short)result.Measurement.SubgroupSize,
                Values = result.Measurement.Values?.ToArray(),
                result.Statistics.Mean,
                result.Statistics.Range,
                result.Statistics.StdDev,
                result.Measurement.Inspected,
                result.Measurement.Defects,
                result.SpecItemId,
                result.Conforms,
                result.Source,
                result.EnteredByUserId,
                result.RecordedBy,
                result.RecordedAt,
                result.Comment,
            },
            cancellationToken);

    public async Task<QualityResultRow?> GetResultForUpdateAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ResultRow>(ResultForUpdate, new { Id = id, OccurredAt = occurredAt }, cancellationToken))?.ToRow();

    public Task ExcludeResultAsync(Guid id, DateTimeOffset occurredAt, Guid reasonId, string? note, string by, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(Exclude, new { Id = id, OccurredAt = occurredAt, ReasonId = reasonId, Note = note, By = by, At = at }, cancellationToken);

    private const string LineCode = "SELECT code FROM core.equipment WHERE id = @Id AND can_run_job";

    private const string ScopeLimits = $"""
        SELECT {QualitySql.LimitColumns} FROM core.control_limit l
        WHERE l.characteristic_id = @CharacteristicId AND l.material_id = @MaterialId AND l.equipment_id IS NOT DISTINCT FROM @EquipmentId
        ORDER BY l.effective_from
        """;

    private const string LimitById = $"SELECT {QualitySql.LimitColumns} FROM core.control_limit l WHERE l.id = @Id";

    private const string OpenLimits = "SELECT EXISTS (SELECT 1 FROM core.control_limit WHERE characteristic_id = @Id AND (effective_to IS NULL OR effective_to > @At))";

    private const string InsertLimit = """
        INSERT INTO core.control_limit (id, characteristic_id, material_id, equipment_id, chart_type, subgroup_size, center_line, ucl, lcl,
                                        dispersion_center, dispersion_ucl, dispersion_lcl, method, baseline_from, baseline_to, baseline_subgroups,
                                        left_out_sample_ids, note, effective_from, effective_to, approved_by, approved_at, created_at, updated_at)
        VALUES (@Id, @CharacteristicId, @MaterialId, @EquipmentId, @ChartType, @SubgroupSize, @Center, @Ucl, @Lcl,
                @DispersionCenter, @DispersionUcl, @DispersionLcl, @Method, @BaselineFrom, @BaselineTo, @BaselineSubgroups,
                @LeftOut, @Note, @EffectiveFrom, @EffectiveTo, @ApprovedBy, @ApprovedAt, @ApprovedAt, @ApprovedAt)
        """;

    private const string EndLimit = "UPDATE core.control_limit SET effective_to = @EndsAt, ended_by = @By, updated_at = @At WHERE id = @Id";

    public Task<string?> GetLineCodeAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<string>(LineCode, new { Id = id }, cancellationToken);

    public async Task<IReadOnlyList<Domain.Quality.Spc.ControlLimit>> GetLimitsAsync(Guid characteristicId, Guid materialId, Guid? equipmentId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<QualitySql.LimitRow>(ScopeLimits, new { CharacteristicId = characteristicId, MaterialId = materialId, EquipmentId = equipmentId }, cancellationToken))
            .Select(r => r.ToDomain())];

    public async Task<Domain.Quality.Spc.ControlLimit?> GetLimitAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<QualitySql.LimitRow>(LimitById, new { Id = id }, cancellationToken))?.ToDomain();

    public Task<bool> HasOpenLimitsAsync(Guid characteristicId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(OpenLimits, new { Id = characteristicId, At = at }, cancellationToken);

    public Task InsertLimitAsync(Domain.Quality.Spc.ControlLimit l, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertLimit, new
        {
            l.Id,
            l.CharacteristicId,
            l.MaterialId,
            l.EquipmentId,
            l.ChartType,
            SubgroupSize = (short)l.SubgroupSize,
            l.Limits.Center,
            l.Limits.Ucl,
            l.Limits.Lcl,
            l.Limits.DispersionCenter,
            l.Limits.DispersionUcl,
            l.Limits.DispersionLcl,
            l.Method,
            BaselineFrom = l.Baseline?.From,
            BaselineTo = l.Baseline?.To,
            BaselineSubgroups = l.Baseline?.Subgroups,
            LeftOut = (l.Baseline?.LeftOut ?? []).ToArray(),
            l.Note,
            l.EffectiveFrom,
            l.EffectiveTo,
            l.ApprovedBy,
            l.ApprovedAt,
        }, cancellationToken);

    public Task EndLimitAsync(Guid id, DateTimeOffset endsAt, string? by, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(EndLimit, new { Id = id, EndsAt = endsAt, By = by, At = at }, cancellationToken);

    public async Task<IReadOnlyList<Domain.Quality.Spc.SpcSubgroup>> ListBaselineAsync(
        Guid characteristicId, Guid materialId, Guid? equipmentId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<QualitySql.BaselineRow>(QualitySql.Baseline, new
        {
            CharacteristicId = characteristicId,
            MaterialId = materialId,
            EquipmentId = equipmentId,
            From = windowStart,
            To = windowEnd,
            Limit = IQualityReadStore.MaxBaseline,
        }, cancellationToken)).Select(r => r.ToSubgroup())];

    // The latest results on exactly this line (or without one) up to a time, the new one last among equals.
    private const string RuleWindow = """
        SELECT * FROM (
            SELECT q.id AS sample_id, q.occurred_at, q.measured_values, q.inspected_count, q.defect_count, q.equipment_id,
                   NULL::text AS line_code, NULL::text AS lot_label, NULL::text AS job_number, (q.id = @LastId) AS is_last
            FROM ts.quality_sample q
            WHERE q.site_id = @SiteId AND q.characteristic_id = @CharacteristicId AND q.material_id = @MaterialId AND NOT q.is_excluded
              AND q.equipment_id IS NOT DISTINCT FROM @EquipmentId AND q.occurred_at <= @At
            ORDER BY q.occurred_at DESC, (q.id = @LastId) DESC, q.id DESC
            LIMIT @Count) w
        ORDER BY occurred_at, is_last, sample_id
        """;

    private const string MaterialLimits = $"SELECT {QualitySql.LimitColumns} FROM core.control_limit l WHERE l.characteristic_id = @CharacteristicId AND l.material_id = @MaterialId";

    private const string RaiseAlert = """
        INSERT INTO core.spc_alert AS a (id, site_id, characteristic_id, material_id, equipment_id, status, rules, breaks,
                                         first_sample_id, first_sample_at, last_sample_id, last_sample_at, raised_at, created_at, updated_at)
        VALUES (@Id, @SiteId, @CharacteristicId, @MaterialId, @EquipmentId, 'open', @Rules, 1,
                @SampleId, @SampleAt, @SampleId, @SampleAt, @At, @At, @At)
        ON CONFLICT (site_id, characteristic_id, material_id, (coalesce(equipment_id, '00000000-0000-0000-0000-000000000000'::uuid))) WHERE status = 'open'
        DO UPDATE SET rules = ARRAY(SELECT DISTINCT r FROM unnest(a.rules || excluded.rules) r ORDER BY r),
                      breaks = a.breaks + 1,
                      last_sample_id = CASE WHEN excluded.last_sample_at >= a.last_sample_at THEN excluded.last_sample_id ELSE a.last_sample_id END,
                      last_sample_at = greatest(a.last_sample_at, excluded.last_sample_at),
                      first_sample_id = CASE WHEN excluded.first_sample_at < a.first_sample_at THEN excluded.first_sample_id ELSE a.first_sample_id END,
                      first_sample_at = least(a.first_sample_at, excluded.first_sample_at),
                      updated_at = excluded.updated_at
        RETURNING a.id AS alert_id, (a.xmax = 0) AS is_new
        """;

    private const string InsertViolation = """
        INSERT INTO ts.spc_violation (id, occurred_at, site_id, characteristic_id, material_id, equipment_id, sample_id, rules, alert_id, recorded_at)
        VALUES (@Id, @SampleAt, @SiteId, @CharacteristicId, @MaterialId, @EquipmentId, @SampleId, @Rules, @AlertId, @At)
        """;

    private const string AlertForUpdate = """
        SELECT id, site_id, characteristic_id, status, rules::int[] AS rules, breaks FROM core.spc_alert WHERE id = @Id FOR UPDATE
        """;

    private const string CloseAlert = """
        UPDATE core.spc_alert
        SET status = 'closed', closed_at = @At, closed_by = @By, cause_reason_code_id = @CauseId, action = @Action, updated_at = @At
        WHERE id = @Id AND status = 'open'
        """;

    public async Task<IReadOnlyList<ChartSample>> ListRuleWindowAsync(
        Guid siteId, Guid characteristicId, Guid materialId, Guid? equipmentId, DateTimeOffset at, Guid lastSampleId, int count, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<QualitySql.ChartSampleRow>(RuleWindow, new
        {
            SiteId = siteId,
            CharacteristicId = characteristicId,
            MaterialId = materialId,
            EquipmentId = equipmentId,
            At = at,
            LastId = lastSampleId,
            Count = count,
        }, cancellationToken)).Select(r => r.ToSample())];

    public async Task<IReadOnlyList<Domain.Quality.Spc.ControlLimit>> GetMaterialLimitsAsync(Guid characteristicId, Guid materialId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<QualitySql.LimitRow>(MaterialLimits, new { CharacteristicId = characteristicId, MaterialId = materialId }, cancellationToken))
            .Select(r => r.ToDomain())];

    public async Task<(Guid AlertId, bool IsNew)> RaiseAlertAsync(SpcBreak signal, Guid newId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var row = (await session.QuerySingleOrDefaultAsync<RaisedRow>(RaiseAlert, ViolationArgs(signal, newId, Guid.Empty, at), cancellationToken))!;
        return (row.AlertId, row.IsNew);
    }

    public Task InsertViolationAsync(SpcBreak signal, Guid id, Guid alertId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertViolation, ViolationArgs(signal, id, alertId, at), cancellationToken);

    public async Task<SpcAlertRecord?> GetAlertForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<AlertRow>(AlertForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new SpcAlertRecord(r.Id, r.SiteId, r.CharacteristicId, r.Status, r.Rules, r.Breaks)
            : null;

    public Task CloseAlertAsync(Guid id, Guid causeReasonId, string action, string by, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(CloseAlert, new { Id = id, CauseId = causeReasonId, Action = action, By = by, At = at }, cancellationToken);

    private static object ViolationArgs(SpcBreak s, Guid id, Guid alertId, DateTimeOffset at) => new
    {
        Id = id,
        s.SiteId,
        s.CharacteristicId,
        s.MaterialId,
        s.EquipmentId,
        s.SampleId,
        s.SampleAt,
        Rules = s.Rules.Select(r => (short)r).ToArray(),
        AlertId = alertId,
        At = at,
    };

    private sealed class RaisedRow
    {
        public Guid AlertId { get; init; }
        public bool IsNew { get; init; }
    }

    private sealed class AlertRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid CharacteristicId { get; init; }
        public string Status { get; init; } = "";
        public int[] Rules { get; init; } = [];
        public int Breaks { get; init; }
    }

    private static object CharacteristicArgs(Characteristic c, DateTimeOffset at) => new
    {
        c.Id,
        c.Code,
        c.Name,
        c.Description,
        c.DataKind,
        c.ChartType,
        SubgroupSize = (short)c.SubgroupSize,
        RunRules = c.RunRules.Select(r => (short)r).ToArray(),
        c.UomId,
        Decimals = (short)c.Decimals,
        c.IsActive,
        At = at,
    };

    private static object VersionArgs(SpecVersion version, DateTimeOffset at) => new
    {
        version.Id,
        version.SpecId,
        version.Number,
        version.Status,
        Definition = ProcessJson.Write(new SpecItemsDocument(version.Definition.Items)),
        version.ReleasedAt,
        version.ReleasedBy,
        version.Note,
        At = at,
    };

    private sealed class MaterialRow
    {
        public string Code { get; init; } = "";
        public bool IsActive { get; init; }
    }

    private sealed class SpecRow
    {
        public Guid Id { get; init; }
        public Guid MaterialId { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid Id { get; init; }
        public Guid CharacteristicId { get; init; }
        public decimal? Lsl { get; init; }
        public decimal? Target { get; init; }
        public decimal? Usl { get; init; }
        public int? MaxDefects { get; init; }
        public bool HoldOnFail { get; init; }
        public int SortOrder { get; init; }
        public int VersionNumber { get; init; }
    }

    private sealed class JobRow
    {
        public Guid JobId { get; init; }
        public string JobNumber { get; init; } = "";
        public Guid SiteId { get; init; }
        public Guid? MaterialId { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid? RunId { get; init; }
        public Guid? RunEquipmentId { get; init; }
    }

    private sealed class ProductionRow
    {
        public Guid JobId { get; init; }
        public Guid? RunId { get; init; }
        public DateTimeOffset EffectiveAt { get; init; }
    }

    private sealed class ResultRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public Guid SiteId { get; init; }
        public Guid CharacteristicId { get; init; }
        public Guid? EquipmentId { get; init; }
        public Guid? JobId { get; init; }
        public Guid? RunId { get; init; }
        public Guid? LotId { get; init; }
        public Guid MaterialId { get; init; }
        public decimal[]? MeasuredValues { get; init; }
        public int? InspectedCount { get; init; }
        public int? DefectCount { get; init; }
        public decimal? Mean { get; init; }
        public decimal? Range { get; init; }
        public decimal? StdDev { get; init; }
        public Guid? SpecItemId { get; init; }
        public bool? Conforms { get; init; }
        public string Source { get; init; } = "";
        public Guid? EnteredByUserId { get; init; }
        public string RecordedBy { get; init; } = "";
        public DateTimeOffset RecordedAt { get; init; }
        public string? Comment { get; init; }
        public bool IsExcluded { get; init; }

        public QualityResultRow ToRow() => new(
            Id, OccurredAt, SiteId, CharacteristicId, EquipmentId, JobId, RunId, LotId, MaterialId,
            new QualityMeasurement(MeasuredValues, InspectedCount, DefectCount), new SampleStatistics(Mean, Range, StdDev),
            SpecItemId, Conforms, Source, EnteredByUserId, RecordedBy, RecordedAt, Comment)
        {
            IsExcluded = IsExcluded,
        };
    }
}
