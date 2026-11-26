using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Approvals;
using Pulaski.Contracts.Approvals;
using Pulaski.Domain.Approvals;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Approvals;

/// <summary>SQL the approval reads and writes share (#138).</summary>
internal static class ReleaseApprovalSql
{
    public const string RequestColumns = """
        id, kind, document_id, version_id, site_ids, status, submitted_by, submitted_at, note, decided_by, decided_at, decision_note
        """;

    // Who saved the version's draft: its draft-saved events on the document, each naming the version.
    public const string Editors = """
        SELECT DISTINCT actor_id FROM evt.event
        WHERE subject = @DocumentId AND type = ANY(@Types) AND data->>'versionId' = @VersionId::text
        """;

    // The sites that can make a material now (a line there has a capability for it), and a process's site.
    public const string MaterialSites = """
        SELECT DISTINCT e.site_id FROM core.equipment_capability c JOIN core.equipment e ON e.id = c.equipment_id
        WHERE c.material_id = @MaterialId AND c.effective_from <= now() AND (c.effective_to IS NULL OR c.effective_to > now()) AND e.site_id IS NOT NULL
        UNION
        SELECT site_id FROM core.process WHERE id = @ProcessId
        """;

    public const string Requiring = """
        SELECT site_id FROM core.release_approval_settings
        WHERE site_id = ANY(@Sites) AND CASE @Kind WHEN 'process' THEN processes WHEN 'bom' THEN boms WHEN 'spec' THEN specs ELSE false END
        """;

    internal sealed class RequestRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "";
        public Guid DocumentId { get; init; }
        public Guid VersionId { get; init; }
        public Guid[] SiteIds { get; init; } = [];
        public string Status { get; init; } = "";
        public string SubmittedBy { get; init; } = "";
        public DateTimeOffset SubmittedAt { get; init; }
        public string? Note { get; init; }
        public string? DecidedBy { get; init; }
        public DateTimeOffset? DecidedAt { get; init; }
        public string? DecisionNote { get; init; }

        public ReleaseRequest ToDomain() => new(Id, Kind, DocumentId, VersionId, SiteIds, Status, SubmittedBy, SubmittedAt, Note, DecidedBy, DecidedAt, DecisionNote);
    }
}

/// <summary>Release approval's writes (#138, D-122), in the command's transaction.</summary>
internal sealed class ReleaseApprovalRepository(DbSession session) : IReleaseApprovalRepository
{
    public async Task<Versioned<ReleaseApprovalSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        // A site without a row reads as version 0 (all off); its row is made now, and that first save still matches 0.
        var created = await session.QuerySingleOrDefaultAsync<int?>(
            "INSERT INTO core.release_approval_settings (site_id) VALUES (@SiteId) ON CONFLICT (site_id) DO NOTHING RETURNING 1", new { SiteId = siteId }, cancellationToken) is not null;
        var row = await session.QuerySingleOrDefaultAsync<(bool Processes, bool Boms, bool Specs, long Version)>(
            "SELECT processes, boms, specs, xmin::text::bigint FROM core.release_approval_settings WHERE site_id = @SiteId FOR UPDATE", new { SiteId = siteId }, cancellationToken);
        return new Versioned<ReleaseApprovalSettings>(new ReleaseApprovalSettings(siteId, row.Processes, row.Boms, row.Specs), created ? 0 : row.Version);
    }

    public Task<long> UpdateSettingsAsync(ReleaseApprovalSettings settings, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            UPDATE core.release_approval_settings SET processes = @Processes, boms = @Boms, specs = @Specs, updated_at = @At
            WHERE site_id = @SiteId RETURNING xmin::text::bigint
            """, new { settings.SiteId, settings.Processes, settings.Boms, settings.Specs, At = at }, cancellationToken);

    public async Task<IReadOnlyList<Guid>> SitesRequiringAsync(string kind, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<Guid>(ReleaseApprovalSql.Requiring, new { Kind = kind, Sites = siteIds.ToArray() }, cancellationToken)];

    public async Task<ReleaseRequest?> GetPendingForVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ReleaseApprovalSql.RequestRow>(
            $"SELECT {ReleaseApprovalSql.RequestColumns} FROM core.release_request WHERE version_id = @Id AND status = 'pending' FOR UPDATE",
            new { Id = versionId }, cancellationToken))?.ToDomain();

    public async Task<ReleaseRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ReleaseApprovalSql.RequestRow>(
            $"SELECT {ReleaseApprovalSql.RequestColumns} FROM core.release_request WHERE id = @Id", new { Id = id }, cancellationToken))?.ToDomain();

    public async Task<ReleaseRequest?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ReleaseApprovalSql.RequestRow>(
            $"SELECT {ReleaseApprovalSql.RequestColumns} FROM core.release_request WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken))?.ToDomain();

    public Task InsertAsync(ReleaseRequest request, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.release_request (id, kind, document_id, version_id, site_ids, status, submitted_by, submitted_at, note)
            VALUES (@Id, @Kind, @DocumentId, @VersionId, @SiteIds, @Status, @SubmittedBy, @SubmittedAt, @Note)
            """,
            new
            {
                request.Id,
                request.Kind,
                request.DocumentId,
                request.VersionId,
                SiteIds = request.SiteIds.ToArray(),
                request.Status,
                request.SubmittedBy,
                request.SubmittedAt,
                request.Note,
            }, cancellationToken);

    public Task UpdateAsync(ReleaseRequest request, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "UPDATE core.release_request SET status = @Status, decided_by = @DecidedBy, decided_at = @DecidedAt, decision_note = @DecisionNote WHERE id = @Id",
            new { request.Id, request.Status, request.DecidedBy, request.DecidedAt, request.DecisionNote }, cancellationToken);

    public async Task<IReadOnlyList<string>> EditorsAsync(Guid documentId, Guid versionId, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<string>(ReleaseApprovalSql.Editors, new { DocumentId = documentId, VersionId = versionId, Types = eventTypes.ToArray() }, cancellationToken)];

    public async Task<(IReadOnlyList<Guid> Sites, string MaterialCode)> MaterialSitesAsync(Guid materialId, Guid? processId, CancellationToken cancellationToken)
    {
        var sites = await session.QueryAsync<Guid>(ReleaseApprovalSql.MaterialSites, new { MaterialId = materialId, ProcessId = processId }, cancellationToken);
        var code = await session.QuerySingleOrDefaultAsync<string>("SELECT code FROM core.material WHERE id = @Id", new { Id = materialId }, cancellationToken);
        return ([.. sites], code ?? "?");
    }
}

/// <summary>Release approval's reads (#138).</summary>
internal sealed class ReleaseApprovalQueries(NpgsqlDataSource dataSource) : IReleaseApprovalReadStore
{
    public async Task<ReleaseApprovalSettingsDto> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(bool Processes, bool Boms, bool Specs, long Version)?>(new CommandDefinition(
            "SELECT processes, boms, specs, xmin::text::bigint FROM core.release_approval_settings WHERE site_id = @SiteId", new { SiteId = siteId }, cancellationToken: cancellationToken));
        return row is { } r
            ? new ReleaseApprovalSettingsDto { SiteId = siteId, Processes = r.Processes, Boms = r.Boms, Specs = r.Specs, Version = r.Version }
            : new ReleaseApprovalSettingsDto { SiteId = siteId };
    }

    public async Task<IReadOnlyList<Guid>> SitesForAsync(string kind, Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return kind switch
        {
            ReleaseKinds.Process => [.. await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT site_id FROM core.process WHERE id = @Id", new { Id = documentId }, cancellationToken: cancellationToken))],
            ReleaseKinds.Bom => [.. await connection.QueryAsync<Guid>(new CommandDefinition(
                $"WITH d AS (SELECT material_id, process_id FROM core.bom WHERE id = @Id) {MaterialSitesOf("d")}", new { Id = documentId }, cancellationToken: cancellationToken))],
            ReleaseKinds.Spec => [.. await connection.QueryAsync<Guid>(new CommandDefinition(
                $"WITH d AS (SELECT material_id, NULL::uuid AS process_id FROM core.material_spec WHERE id = @Id) {MaterialSitesOf("d")}", new { Id = documentId }, cancellationToken: cancellationToken))],
            _ => [],
        };
    }

    public async Task<bool> AnyRequiresAsync(string kind, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            ReleaseApprovalSql.Requiring, new { Kind = kind, Sites = siteIds.ToArray() }, cancellationToken: cancellationToken))).Any();
    }

    public async Task<IReadOnlyList<(ReleaseRequest Request, int VersionNumber, string? SubmittedByName, string? DecidedByName)>> ListAsync(
        string kind, Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ListedRow>(new CommandDefinition(
            $"""
            SELECT r.id, r.kind, r.document_id, r.version_id, r.site_ids, r.status, r.submitted_by, r.submitted_at, r.note, r.decided_by, r.decided_at, r.decision_note,
                   COALESCE(pv.version_number, bv.version_number, sv.version_number, 0) AS version_number,
                   {Instructions.InstructionSql.UserName("r.submitted_by")} AS submitted_by_name,
                   {Instructions.InstructionSql.UserName("r.decided_by")} AS decided_by_name
            FROM core.release_request r
            LEFT JOIN core.process_version pv ON r.kind = 'process' AND pv.id = r.version_id
            LEFT JOIN core.bom_version bv ON r.kind = 'bom' AND bv.id = r.version_id
            LEFT JOIN core.material_spec_version sv ON r.kind = 'spec' AND sv.id = r.version_id
            WHERE r.kind = @Kind AND r.document_id = @Id
            ORDER BY r.submitted_at DESC
            """, new { Kind = kind, Id = documentId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => (r.ToDomain(), r.VersionNumber, r.SubmittedByName, r.DecidedByName))];
    }

    public async Task<IReadOnlyList<string>> EditorsAsync(Guid documentId, Guid versionId, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>(new CommandDefinition(
            ReleaseApprovalSql.Editors, new { DocumentId = documentId, VersionId = versionId, Types = eventTypes.ToArray() }, cancellationToken: cancellationToken))];
    }

    // The sites making the document's material (in CTE d: material_id, process_id), as the write side works them out.
    private static string MaterialSitesOf(string cte) => $"""
        SELECT DISTINCT e.site_id FROM {cte} JOIN core.equipment_capability c ON c.material_id = {cte}.material_id JOIN core.equipment e ON e.id = c.equipment_id
        WHERE c.effective_from <= now() AND (c.effective_to IS NULL OR c.effective_to > now()) AND e.site_id IS NOT NULL
        UNION
        SELECT p.site_id FROM {cte} JOIN core.process p ON p.id = {cte}.process_id
        """;

    private sealed class ListedRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "";
        public Guid DocumentId { get; init; }
        public Guid VersionId { get; init; }
        public Guid[] SiteIds { get; init; } = [];
        public string Status { get; init; } = "";
        public string SubmittedBy { get; init; } = "";
        public DateTimeOffset SubmittedAt { get; init; }
        public string? Note { get; init; }
        public string? DecidedBy { get; init; }
        public DateTimeOffset? DecidedAt { get; init; }
        public string? DecisionNote { get; init; }
        public int VersionNumber { get; init; }
        public string? SubmittedByName { get; init; }
        public string? DecidedByName { get; init; }

        public ReleaseRequest ToDomain() => new(Id, Kind, DocumentId, VersionId, SiteIds, Status, SubmittedBy, SubmittedAt, Note, DecidedBy, DecidedAt, DecisionNote);
    }
}
