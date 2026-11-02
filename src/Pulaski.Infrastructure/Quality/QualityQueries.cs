using Dapper;
using Npgsql;
using Pulaski.Application.Quality;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;
using Pulaski.Infrastructure.Processes;

namespace Pulaski.Infrastructure.Quality;

/// <summary>Read side of quality (#65, D-085): one connection per query, no locks.</summary>
internal sealed class QualityQueries(NpgsqlDataSource dataSource) : IQualityReadStore
{
    private const string ListCharacteristics = $"""
        SELECT {QualitySql.CharacteristicColumns}, u.code AS uom_code
        FROM core.quality_characteristic c
        LEFT JOIN core.uom u ON u.id = c.uom_id
        WHERE c.site_id IS NULL
        ORDER BY c.is_active DESC, c.code
        """;

    private const string ListSpecs = """
        SELECT s.id, s.material_id, m.code AS material_code, m.name AS material_name, s.is_active, s.updated_at,
               (SELECT v.version_number FROM core.material_spec_version v WHERE v.spec_id = s.id AND v.status = 'released') AS released_version,
               (SELECT v.version_number FROM core.material_spec_version v WHERE v.spec_id = s.id AND v.status = 'draft') AS draft_version,
               (SELECT count(*)::int FROM core.material_spec_item i JOIN core.material_spec_version v ON v.id = i.spec_version_id
                WHERE v.spec_id = s.id AND v.status = 'released') AS released_items
        FROM core.material_spec s
        JOIN core.material m ON m.id = s.material_id
        ORDER BY m.code
        """;

    private const string GetSpecSql = """
        SELECT s.id, s.material_id, m.code AS material_code, m.name AS material_name, s.is_active, s.xmin::text::bigint AS version
        FROM core.material_spec s
        JOIN core.material m ON m.id = s.material_id
        WHERE s.id = @Id
        """;

    private const string Versions = """
        SELECT id, version_number AS number, status, released_at, released_by, note
        FROM core.material_spec_version
        WHERE spec_id = @Id
        ORDER BY version_number DESC
        """;

    private const string GetVersionSql = $"""
        SELECT {QualitySql.VersionColumns} FROM core.material_spec_version v WHERE v.spec_id = @SpecId AND v.id = @VersionId
        """;

    private const string Released = """
        SELECT m.id AS material_id, m.code AS material_code, v.version_number, v.released_at,
               i.id AS item_id, i.characteristic_id, c.code, c.name, c.data_kind, u.code AS uom_code,
               i.lsl, i.target, i.usl, i.max_defects, i.hold_on_fail, i.sort_order
        FROM core.material_spec s
        JOIN core.material m ON m.id = s.material_id
        JOIN core.material_spec_version v ON v.spec_id = s.id AND v.status = 'released'
        JOIN core.material_spec_item i ON i.spec_version_id = v.id
        JOIN core.quality_characteristic c ON c.id = i.characteristic_id
        LEFT JOIN core.uom u ON u.id = c.uom_id
        WHERE s.material_id = @MaterialId AND s.is_active
        ORDER BY i.sort_order, c.code
        """;

    private const string ResultColumns = """
        SELECT q.id, q.occurred_at, q.site_id, q.characteristic_id, c.code AS characteristic_code, c.name AS characteristic_name, c.data_kind,
               u.code AS uom_code, q.material_id, m.code AS material_code, q.lot_id, coalesce(l.lot_number, 'Balance at ' || loc.code) AS lot_label,
               l.status AS lot_status, q.job_id, j.job_number, e.code AS equipment_code, q.measured_values, q.mean, q.inspected_count, q.defect_count,
               i.lsl, i.target, i.usl, i.max_defects, i.id AS spec_item_id, v.version_number AS spec_version_number, q.conforms, q.source,
               q.recorded_by, q.recorded_at, q.comment, q.is_excluded, r.name AS exclusion_reason, q.excluded_note, q.excluded_by, q.excluded_at
        FROM ts.quality_sample q
        JOIN core.quality_characteristic c ON c.id = q.characteristic_id
        JOIN core.material m ON m.id = q.material_id
        LEFT JOIN core.uom u ON u.id = c.uom_id
        LEFT JOIN core.lot l ON l.id = q.lot_id
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        LEFT JOIN core.job j ON j.id = q.job_id
        LEFT JOIN core.equipment e ON e.id = q.equipment_id
        LEFT JOIN core.material_spec_item i ON i.id = q.spec_item_id
        LEFT JOIN core.material_spec_version v ON v.id = i.spec_version_id
        LEFT JOIN core.reason_code r ON r.id = q.exclusion_reason_id
        """;

    private const string ListResults = ResultColumns + """

        WHERE (@AllSites OR q.site_id = ANY(@SiteIds))
          AND (@SiteId::uuid IS NULL OR q.site_id = @SiteId)
          AND (@MaterialId::uuid IS NULL OR q.material_id = @MaterialId)
          AND (@LotId::uuid IS NULL OR q.lot_id = @LotId)
          AND (@JobId::uuid IS NULL OR q.job_id = @JobId)
          AND (@CharacteristicId::uuid IS NULL OR q.characteristic_id = @CharacteristicId)
          AND (NOT @FailingOnly OR (q.conforms = false AND NOT q.is_excluded))
          AND (@From::timestamptz IS NULL OR q.occurred_at >= @From)
          AND (@To::timestamptz IS NULL OR q.occurred_at < @To)
        ORDER BY q.occurred_at DESC, q.id
        LIMIT @Limit
        """;

    private const string GetResult = ResultColumns + "\nWHERE q.id = @Id AND q.occurred_at = @OccurredAt";

    public async Task<IReadOnlyList<CharacteristicDto>> ListCharacteristicsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CharacteristicListRow>(new CommandDefinition(ListCharacteristics, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new CharacteristicDto
        {
            Id = r.Id,
            Code = r.Code,
            Name = r.Name,
            Description = r.Description,
            DataKind = r.DataKind,
            UomId = r.UomId,
            UomCode = r.UomCode,
            Decimals = r.Decimals,
            IsActive = r.IsActive,
            Version = r.Version,
        })];
    }

    public async Task<IReadOnlyList<Characteristic>> GetCharacteristicsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<QualitySql.CharacteristicRow>(
            new CommandDefinition(QualitySql.Characteristics, new { Ids = ids.ToArray() }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<SpecSummaryDto>> ListSpecsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<SpecSummaryDto>(new CommandDefinition(ListSpecs, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<SpecDto?> GetSpecAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var spec = await connection.QuerySingleOrDefaultAsync<SpecDto>(new CommandDefinition(GetSpecSql, new { Id = id }, cancellationToken: cancellationToken));
        if (spec is null)
        {
            return null;
        }

        var versions = await connection.QueryAsync<VersionSummaryRow>(new CommandDefinition(Versions, new { Id = id }, cancellationToken: cancellationToken));
        return spec with { Versions = [.. versions.Select(v => new SpecVersionSummaryDto(v.Id, v.Number, v.Status, v.ReleasedAt, v.ReleasedBy, v.Note))] };
    }

    public async Task<SpecVersionDto?> GetVersionAsync(Guid specId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<QualitySql.VersionRow>(
            new CommandDefinition(GetVersionSql, new { SpecId = specId, VersionId = versionId }, cancellationToken: cancellationToken));
        return row is null ? null : new SpecVersionDto
        {
            Id = row.Id,
            SpecId = row.SpecId,
            Number = row.Number,
            Status = row.Status,
            ReleasedAt = row.ReleasedAt,
            ReleasedBy = row.ReleasedBy,
            Note = row.Note,
            Version = row.Version,
            Definition = new SpecDefinitionDto { Items = ProcessJson.Read<ItemsDocument>(row.DefinitionJson).Items },
        };
    }

    public async Task<ReleasedSpecDto?> GetReleasedSpecAsync(Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ReleasedRow>(new CommandDefinition(Released, new { MaterialId = materialId }, cancellationToken: cancellationToken))).AsList();
        if (rows.Count == 0)
        {
            return null;
        }

        return new ReleasedSpecDto
        {
            MaterialId = rows[0].MaterialId,
            MaterialCode = rows[0].MaterialCode,
            VersionNumber = rows[0].VersionNumber,
            ReleasedAt = rows[0].ReleasedAt,
            Items = [.. rows.Select(r => new ReleasedSpecItemDto
            {
                ItemId = r.ItemId,
                CharacteristicId = r.CharacteristicId,
                Code = r.Code,
                Name = r.Name,
                DataKind = r.DataKind,
                UomCode = r.UomCode,
                Lsl = r.Lsl,
                Target = r.Target,
                Usl = r.Usl,
                MaxDefects = r.MaxDefects,
                HoldOnFail = r.HoldOnFail,
                Limits = QualityCheck.Limits(new SpecItem(r.ItemId, r.CharacteristicId, r.Lsl, r.Target, r.Usl, r.MaxDefects, r.HoldOnFail, r.SortOrder)),
            })],
        };
    }

    public async Task<QualityResultDto?> GetResultAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ResultRow>(
            new CommandDefinition(GetResult, new { Id = id, OccurredAt = occurredAt }, cancellationToken: cancellationToken));
        return row?.ToDto();
    }

    public async Task<IReadOnlyList<QualityResultDto>> ListResultsAsync(QualityResultFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ResultRow>(new CommandDefinition(
            ListResults,
            new
            {
                AllSites = filter.SiteIds is null,
                SiteIds = (filter.SiteIds ?? []).ToArray(),
                filter.SiteId,
                filter.MaterialId,
                filter.LotId,
                filter.JobId,
                filter.CharacteristicId,
                filter.FailingOnly,
                filter.From,
                filter.To,
                filter.Limit,
            },
            cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToDto())];
    }

    private sealed record ItemsDocument(IReadOnlyList<SpecItemDto> Items);

    private sealed class CharacteristicListRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string DataKind { get; init; } = "";
        public Guid? UomId { get; init; }
        public string? UomCode { get; init; }
        public int Decimals { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class VersionSummaryRow
    {
        public Guid Id { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? ReleasedBy { get; init; }
        public string? Note { get; init; }
    }

    private sealed class ReleasedRow
    {
        public Guid MaterialId { get; init; }
        public string MaterialCode { get; init; } = "";
        public int VersionNumber { get; init; }
        public DateTimeOffset? ReleasedAt { get; init; }
        public Guid ItemId { get; init; }
        public Guid CharacteristicId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string DataKind { get; init; } = "";
        public string? UomCode { get; init; }
        public decimal? Lsl { get; init; }
        public decimal? Target { get; init; }
        public decimal? Usl { get; init; }
        public int? MaxDefects { get; init; }
        public bool HoldOnFail { get; init; }
        public int SortOrder { get; init; }
    }

    private sealed class ResultRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public Guid SiteId { get; init; }
        public Guid CharacteristicId { get; init; }
        public string CharacteristicCode { get; init; } = "";
        public string CharacteristicName { get; init; } = "";
        public string DataKind { get; init; } = "";
        public string? UomCode { get; init; }
        public Guid MaterialId { get; init; }
        public string MaterialCode { get; init; } = "";
        public Guid? LotId { get; init; }
        public string? LotLabel { get; init; }
        public string? LotStatus { get; init; }
        public Guid? JobId { get; init; }
        public string? JobNumber { get; init; }
        public string? EquipmentCode { get; init; }
        public decimal[]? MeasuredValues { get; init; }
        public decimal? Mean { get; init; }
        public int? InspectedCount { get; init; }
        public int? DefectCount { get; init; }
        public decimal? Lsl { get; init; }
        public decimal? Target { get; init; }
        public decimal? Usl { get; init; }
        public int? MaxDefects { get; init; }
        public Guid? SpecItemId { get; init; }
        public int? SpecVersionNumber { get; init; }
        public bool? Conforms { get; init; }
        public string Source { get; init; } = "";
        public string RecordedBy { get; init; } = "";
        public DateTimeOffset RecordedAt { get; init; }
        public string? Comment { get; init; }
        public bool IsExcluded { get; init; }
        public string? ExclusionReason { get; init; }
        public string? ExcludedNote { get; init; }
        public string? ExcludedBy { get; init; }
        public DateTimeOffset? ExcludedAt { get; init; }

        public QualityResultDto ToDto() => new()
        {
            Id = Id,
            OccurredAt = OccurredAt,
            SiteId = SiteId,
            CharacteristicId = CharacteristicId,
            CharacteristicCode = CharacteristicCode,
            CharacteristicName = CharacteristicName,
            DataKind = DataKind,
            UomCode = UomCode,
            MaterialId = MaterialId,
            MaterialCode = MaterialCode,
            LotId = LotId,
            LotLabel = LotId is null ? null : LotLabel,
            LotStatus = LotStatus,
            JobId = JobId,
            JobNumber = JobNumber,
            EquipmentCode = EquipmentCode,
            Values = MeasuredValues ?? [],
            Mean = Mean,
            Inspected = InspectedCount,
            Defects = DefectCount,
            Limits = SpecItemId is { } itemId ? QualityCheck.Limits(new SpecItem(itemId, CharacteristicId, Lsl, Target, Usl, MaxDefects, false, 0)) : null,
            SpecVersionNumber = SpecVersionNumber,
            Conforms = Conforms,
            Source = Source,
            RecordedBy = RecordedBy,
            RecordedAt = RecordedAt,
            Comment = Comment,
            IsExcluded = IsExcluded,
            ExclusionReason = ExclusionReason,
            ExcludedNote = ExcludedNote,
            ExcludedBy = ExcludedBy,
            ExcludedAt = ExcludedAt,
        };
    }
}
