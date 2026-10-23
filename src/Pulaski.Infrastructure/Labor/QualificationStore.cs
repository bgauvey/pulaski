using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Labor;

/// <summary>Write side for qualifications (D-048). Nothing is deleted: grants are revoked, requirements switched off.</summary>
internal sealed class QualificationRepository(DbSession session) : IQualificationRepository
{
    private const string Get = """
        SELECT id, site_id, code, name, description, validity_months, is_active, xmin::text::bigint AS version
        FROM core.qualification WHERE id = @Id FOR UPDATE
        """;

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM core.qualification WHERE site_id = @SiteId AND code = @Code)";

    private const string Insert = """
        INSERT INTO core.qualification (id, site_id, code, name, description, validity_months, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @Description, @ValidityMonths, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.qualification SET name = @Name, description = @Description, validity_months = @ValidityMonths, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string GetGrant = $"SELECT {LaborSql.GrantColumns} FROM core.person_qualification g WHERE g.id = @Id FOR UPDATE";

    private const string InsertGrant = """
        INSERT INTO core.person_qualification (id, person_id, qualification_id, valid_from, expires_on, note, granted_by, created_at, updated_at)
        VALUES (@Id, @PersonId, @QualificationId, @ValidFrom, @ExpiresOn, @Note, @GrantedBy, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateGrant = """
        UPDATE core.person_qualification
        SET valid_from = @ValidFrom, expires_on = @ExpiresOn, note = @Note, revoked_at = @RevokedAt, revoked_by = @RevokedBy, revoke_reason = @RevokeReason, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string GetRequirement = """
        SELECT is_required FROM core.equipment_qualification WHERE equipment_id = @EquipmentId AND qualification_id = @QualificationId FOR UPDATE
        """;

    private const string SetRequirement = """
        INSERT INTO core.equipment_qualification (equipment_id, qualification_id, is_required, updated_at)
        VALUES (@EquipmentId, @QualificationId, @IsRequired, @At)
        ON CONFLICT (equipment_id, qualification_id) DO UPDATE SET is_required = EXCLUDED.is_required, updated_at = EXCLUDED.updated_at
        RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<QualificationRecord>?> GetQualificationForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<QualificationRow>(Get, new { Id = id }, cancellationToken) is { } r
            ? new(new QualificationRecord(r.Id, r.SiteId, r.Code, r.Name, r.Description, r.ValidityMonths, r.IsActive), r.Version)
            : null;

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<long> InsertQualificationAsync(QualificationRecord q, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, new { q.Id, q.SiteId, q.Code, q.Name, q.Description, q.ValidityMonths, q.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateQualificationAsync(QualificationRecord q, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, new { q.Id, q.Name, q.Description, q.ValidityMonths, q.IsActive, At = at }, cancellationToken);

    public async Task<Versioned<GrantRecord>?> GetGrantForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LaborSql.GrantDbRow>(GetGrant, new { Id = id }, cancellationToken) is { } r ? new(r.ToRecord(), r.Version) : null;

    public Task<long> InsertGrantAsync(GrantRecord g, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertGrant, new { g.Id, g.PersonId, g.QualificationId, g.ValidFrom, g.ExpiresOn, g.Note, g.GrantedBy, At = at }, cancellationToken);

    public Task<long> UpdateGrantAsync(GrantRecord g, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateGrant, new { g.Id, g.ValidFrom, g.ExpiresOn, g.Note, g.RevokedAt, g.RevokedBy, g.RevokeReason, At = at }, cancellationToken);

    public Task<bool?> GetRequirementForUpdateAsync(Guid equipmentId, Guid qualificationId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<bool?>(GetRequirement, new { EquipmentId = equipmentId, QualificationId = qualificationId }, cancellationToken);

    public Task<long> SetRequirementAsync(Guid equipmentId, Guid qualificationId, bool isRequired, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(SetRequirement, new { EquipmentId = equipmentId, QualificationId = qualificationId, IsRequired = isRequired, At = at }, cancellationToken);

    private sealed class QualificationRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public int? ValidityMonths { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }
}

/// <summary>Read side for qualifications (D-048).</summary>
internal sealed class QualificationQueries(NpgsqlDataSource dataSource) : IQualificationReadStore
{
    private const string List = """
        SELECT q.id, q.site_id, q.code, q.name, q.description, q.validity_months, q.is_active, q.xmin::text::bigint AS version,
               coalesce((SELECT array_agg(e.code ORDER BY e.code) FROM core.equipment_qualification eq JOIN core.equipment e ON e.id = eq.equipment_id
                         WHERE eq.qualification_id = q.id AND eq.is_required), '{}') AS required_on
        FROM core.qualification q
        WHERE q.site_id = @SiteId
        ORDER BY q.is_active DESC, q.code
        """;

    private const string Grants = $"""
        SELECT {LaborSql.GrantColumns}, p.display_name AS person_name, p.employee_number, p.site_id, q.code AS qualification_code, q.name AS qualification_name
        FROM core.person_qualification g
        JOIN core.person p ON p.id = g.person_id
        JOIN core.qualification q ON q.id = g.qualification_id
        WHERE p.site_id = @SiteId
        """;

    private const string Exists = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id)";

    private const string Requirements = $"{LaborSql.Requirements} WHERE c.descendant_id = @Id ORDER BY c.depth, q.code";

    public async Task<IReadOnlyList<QualificationDto>> ListQualificationsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<ListRow>(new CommandDefinition(List, new { SiteId = siteId }, cancellationToken: cancellationToken)))
            .Select(r => new QualificationDto
            {
                Id = r.Id,
                SiteId = r.SiteId,
                Code = r.Code,
                Name = r.Name,
                Description = r.Description,
                ValidityMonths = r.ValidityMonths,
                IsActive = r.IsActive,
                RequiredOn = r.RequiredOn,
                Version = r.Version,
            })];
    }

    public async Task<IReadOnlyList<GrantRow>> ListGrantsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<GrantViewRow>(new CommandDefinition(Grants, new { SiteId = siteId }, cancellationToken: cancellationToken)))
            .Select(r => new GrantRow(r.ToRecord(), r.PersonName, r.EmployeeNumber, r.SiteId, r.QualificationCode, r.QualificationName, r.Version))];
    }

    public async Task<IReadOnlyList<RequirementRow>?> ListRequirementsAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(Exists, new { Id = equipmentId }, cancellationToken: cancellationToken)))
        {
            return null;
        }

        return [.. (await connection.QueryAsync<LaborSql.RequirementDbRow>(new CommandDefinition(Requirements, new { Id = equipmentId }, cancellationToken: cancellationToken)))
            .Select(r => new RequirementRow(r.QualificationId, r.Code, r.Name, r.FromEquipmentId, r.FromEquipmentCode, r.FromEquipmentId == equipmentId))];
    }

    private sealed class ListRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public int? ValidityMonths { get; init; }
        public bool IsActive { get; init; }
        public string[] RequiredOn { get; init; } = [];
        public long Version { get; init; }
    }

    private sealed class GrantViewRow : LaborSql.GrantDbRow
    {
        public string PersonName { get; init; } = "";
        public string EmployeeNumber { get; init; } = "";
        public Guid SiteId { get; init; }
        public string QualificationCode { get; init; } = "";
        public string QualificationName { get; init; } = "";
    }
}
