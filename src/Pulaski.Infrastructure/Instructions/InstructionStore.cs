using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Instructions;
using Pulaski.Contracts.Instructions;
using Pulaski.Domain.Instructions;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Instructions;

/// <summary>Work instructions' writes (#126a, D-116), in the command's transaction.</summary>
internal sealed class InstructionRepository(DbSession session) : IInstructionRepository
{
    private const string VersionColumns = """
        id, instruction_id, version_number AS number, status, body, released_at, released_by, note, checklists::text AS checklists_json,
        xmin::text::bigint AS version
        """;

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')", new { Id = siteId }, cancellationToken);

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM core.work_instruction WHERE site_id = @SiteId AND code = @Code)", new { SiteId = siteId, Code = code }, cancellationToken);

    public async Task<Versioned<WorkInstruction>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<InstructionRow>(
            "SELECT id, site_id, code, title, is_active, xmin::text::bigint AS version FROM core.work_instruction WHERE id = @Id FOR UPDATE",
            new { Id = id }, cancellationToken) is { } r
            ? new Versioned<WorkInstruction>(new WorkInstruction(r.Id, r.SiteId, r.Code, r.Title, r.IsActive), r.Version)
            : null;

    public Task<long> InsertAsync(WorkInstruction instruction, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            INSERT INTO core.work_instruction (id, site_id, code, title, is_active, created_at, updated_at)
            VALUES (@Id, @SiteId, @Code, @Title, @IsActive, @At, @At)
            RETURNING xmin::text::bigint
            """,
            new { instruction.Id, instruction.SiteId, instruction.Code, instruction.Title, instruction.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateAsync(WorkInstruction instruction, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            "UPDATE core.work_instruction SET title = @Title, is_active = @IsActive, updated_at = @At WHERE id = @Id RETURNING xmin::text::bigint",
            new { instruction.Id, instruction.Title, instruction.IsActive, At = at }, cancellationToken);

    public async Task<Versioned<InstructionVersion>?> GetDraftAsync(Guid instructionId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<VersionRow>(
            $"SELECT {VersionColumns} FROM core.work_instruction_version WHERE instruction_id = @Id AND status = 'draft' FOR UPDATE",
            new { Id = instructionId }, cancellationToken) is { } r
            ? new Versioned<InstructionVersion>(r.ToDomain(), r.Version)
            : null;

    public async Task<InstructionVersion?> GetReleasedAsync(Guid instructionId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<VersionRow>(
            $"SELECT {VersionColumns} FROM core.work_instruction_version WHERE instruction_id = @Id AND status = 'released' FOR UPDATE",
            new { Id = instructionId }, cancellationToken))?.ToDomain();

    public Task<long> InsertVersionAsync(InstructionVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            INSERT INTO core.work_instruction_version (id, instruction_id, version_number, status, body, checklists, created_at, updated_at)
            VALUES (@Id, @InstructionId, @Number, @Status, @Body, @Checklists::jsonb, @At, @At)
            RETURNING xmin::text::bigint
            """,
            new { version.Id, version.InstructionId, version.Number, version.Status, version.Body, Checklists = ChecklistJson.Write(version.Checklists), At = at },
            cancellationToken);

    public Task<long> UpdateVersionAsync(InstructionVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            UPDATE core.work_instruction_version
            SET status = @Status, body = @Body, checklists = @Checklists::jsonb, released_at = @ReleasedAt, released_by = @ReleasedBy, note = @Note,
                updated_at = @At
            WHERE id = @Id
            RETURNING xmin::text::bigint
            """,
            new
            {
                version.Id,
                version.Status,
                version.Body,
                Checklists = ChecklistJson.Write(version.Checklists),
                version.ReleasedAt,
                version.ReleasedBy,
                version.Note,
                At = at,
            },
            cancellationToken);

    public async Task<IReadOnlyList<(Guid Id, Guid ContentId, string FileName, string ContentType, int SortOrder)>> ListFilesAsync(Guid versionId, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<(Guid, Guid, string, string, int)>(
            "SELECT id, content_id, file_name, content_type, sort_order FROM core.work_instruction_file WHERE version_id = @Id AND removed_at IS NULL ORDER BY sort_order, id",
            new { Id = versionId }, cancellationToken)];

    public async Task<Guid> InsertContentAsync(Guid id, byte[] content, string sha256, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync(
            "INSERT INTO core.work_instruction_content (id, sha256, size_bytes, content) VALUES (@Id, @Sha256, @Size, @Content)",
            new { Id = id, Sha256 = sha256, Size = content.Length, Content = content }, cancellationToken);
        return id;
    }

    public Task InsertFileAsync(Guid id, Guid versionId, Guid contentId, string fileName, string contentType, int sortOrder, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.work_instruction_file (id, version_id, content_id, file_name, content_type, sort_order)
            VALUES (@Id, @VersionId, @ContentId, @FileName, @ContentType, @SortOrder)
            """,
            new { Id = id, VersionId = versionId, ContentId = contentId, FileName = fileName, ContentType = contentType, SortOrder = sortOrder }, cancellationToken);

    public async Task<bool> RemoveFileAsync(Guid versionId, Guid fileId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.ExecuteAsync(
            "UPDATE core.work_instruction_file SET removed_at = @At WHERE id = @Id AND version_id = @VersionId AND removed_at IS NULL",
            new { Id = fileId, VersionId = versionId, At = at }, cancellationToken) == 1;

    public async Task<IReadOnlyList<InstructionTarget>> ListTargetsAsync(Guid instructionId, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<InstructionTarget>(
            "SELECT id, instruction_id, kind, process_id, operation_number, material_id, equipment_id, is_active FROM core.work_instruction_target WHERE instruction_id = @Id",
            new { Id = instructionId }, cancellationToken)];

    public Task<bool> TargetExistsAsync(Guid siteId, InstructionTarget target, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            target.Kind switch
            {
                InstructionTargetKinds.Operation => """
                    SELECT EXISTS (
                        SELECT 1 FROM core.process p
                        JOIN core.process_version v ON v.process_id = p.id
                        JOIN core.process_operation o ON o.process_version_id = v.id
                        WHERE p.id = @ProcessId AND p.site_id = @SiteId AND o.operation_number = @OperationNumber)
                    """,
                InstructionTargetKinds.Material => "SELECT EXISTS (SELECT 1 FROM core.material WHERE id = @MaterialId)",
                _ => "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @EquipmentId AND site_id = @SiteId AND can_run_job)",
            },
            new { SiteId = siteId, target.ProcessId, target.OperationNumber, target.MaterialId, target.EquipmentId }, cancellationToken);

    public Task InsertTargetAsync(InstructionTarget target, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.work_instruction_target (id, instruction_id, kind, process_id, operation_number, material_id, equipment_id, is_active, created_at, updated_at)
            VALUES (@Id, @InstructionId, @Kind, @ProcessId, @OperationNumber, @MaterialId, @EquipmentId, true, @At, @At)
            """,
            new { target.Id, target.InstructionId, target.Kind, target.ProcessId, target.OperationNumber, target.MaterialId, target.EquipmentId, At = at }, cancellationToken);

    public async Task<bool> EndTargetAsync(Guid instructionId, Guid targetId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.ExecuteAsync(
            "UPDATE core.work_instruction_target SET is_active = false, updated_at = @At WHERE id = @Id AND instruction_id = @InstructionId AND is_active",
            new { Id = targetId, InstructionId = instructionId, At = at }, cancellationToken) == 1;

    private sealed class InstructionRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Title { get; init; } = "";
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class VersionRow
    {
        public Guid Id { get; init; }
        public Guid InstructionId { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public string Body { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? ReleasedBy { get; init; }
        public string? Note { get; init; }
        public string ChecklistsJson { get; init; } = "[]";
        public long Version { get; init; }

        public InstructionVersion ToDomain() => new(Id, InstructionId, Number, Status, Body, ReleasedAt, ReleasedBy, Note) { Checklists = ChecklistJson.Read(ChecklistsJson) };
    }
}

/// <summary>Work instructions' reads (#126a, D-116).</summary>
internal sealed class InstructionQueries(NpgsqlDataSource dataSource) : IInstructionReadStore
{
    // What a target names, in words: "EXTRUDE op 20", "PVC-100", "EXT-01".
    private const string TargetSelect = """
        SELECT t.id, t.instruction_id, t.kind, t.process_id, p.code AS process_code, t.operation_number,
               (SELECT o.name FROM core.process_version v JOIN core.process_operation o ON o.process_version_id = v.id
                WHERE v.process_id = t.process_id AND o.operation_number = t.operation_number ORDER BY v.version_number DESC LIMIT 1) AS operation_name,
               t.material_id, m.code AS material_code, t.equipment_id, e.code AS equipment_code
        FROM core.work_instruction_target t
        LEFT JOIN core.process p ON p.id = t.process_id
        LEFT JOIN core.material m ON m.id = t.material_id
        LEFT JOIN core.equipment e ON e.id = t.equipment_id
        WHERE t.is_active
        """;

    private const string List = """
        SELECT i.id, i.site_id, i.code, i.title, i.is_active, i.xmin::text::bigint AS version,
               r.version_number AS released_number, r.released_at, d.version_number AS draft_number
        FROM core.work_instruction i
        LEFT JOIN core.work_instruction_version r ON r.instruction_id = i.id AND r.status = 'released'
        LEFT JOIN core.work_instruction_version d ON d.instruction_id = i.id AND d.status = 'draft'
        WHERE i.site_id = @SiteId
        ORDER BY i.is_active DESC, i.code
        """;

    public async Task<IReadOnlyList<InstructionSummaryDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<InstructionSummaryDto>(new CommandDefinition(List, new { SiteId = siteId }, cancellationToken: cancellationToken))).AsList();
        var targets = (await connection.QueryAsync<TargetRow>(new CommandDefinition(
            TargetSelect + " AND t.instruction_id IN (SELECT id FROM core.work_instruction WHERE site_id = @SiteId)",
            new { SiteId = siteId }, cancellationToken: cancellationToken))).ToLookup(t => t.InstructionId);
        return [.. rows.Select(r => r with { Targets = [.. targets[r.Id].Select(t => t.Label())] })];
    }

    public async Task<InstructionDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var instruction = await connection.QuerySingleOrDefaultAsync<InstructionDto>(new CommandDefinition(
            "SELECT id, site_id, code, title, is_active, xmin::text::bigint AS version FROM core.work_instruction WHERE id = @Id",
            new { Id = id }, cancellationToken: cancellationToken));
        if (instruction is null)
        {
            return null;
        }

        var versions = (await connection.QueryAsync<VersionDtoRow>(new CommandDefinition(
            $"""
            SELECT v.id, v.version_number AS number, v.status, v.body, v.released_at, v.released_by, v.note, v.xmin::text::bigint AS version,
                   v.checklists::text AS checklists_json,
                   {InstructionSql.UserName("v.released_by")} AS released_by_name
            FROM core.work_instruction_version v
            WHERE v.instruction_id = @Id
            ORDER BY v.version_number DESC
            """, new { Id = id }, cancellationToken: cancellationToken))).AsList();
        var files = (await connection.QueryAsync<FileRow>(new CommandDefinition(
            """
            SELECT f.id, f.version_id, f.file_name, f.content_type, c.size_bytes
            FROM core.work_instruction_file f
            JOIN core.work_instruction_content c ON c.id = f.content_id
            JOIN core.work_instruction_version v ON v.id = f.version_id
            WHERE v.instruction_id = @Id AND f.removed_at IS NULL
            ORDER BY f.sort_order, f.id
            """, new { Id = id }, cancellationToken: cancellationToken))).ToLookup(f => f.VersionId);
        var targets = await connection.QueryAsync<TargetRow>(new CommandDefinition(
            TargetSelect + " AND t.instruction_id = @Id ORDER BY t.kind, t.created_at", new { Id = id }, cancellationToken: cancellationToken));
        return instruction with
        {
            Versions = [.. versions.Select(v => new InstructionVersionDto
            {
                Id = v.Id,
                Number = v.Number,
                Status = v.Status,
                Body = v.Body,
                ReleasedAt = v.ReleasedAt,
                ReleasedBy = v.ReleasedBy,
                ReleasedByName = v.ReleasedByName,
                Note = v.Note,
                Version = v.Version,
                Files = [.. files[v.Id].Select(f => new InstructionFileDto { Id = f.Id, FileName = f.FileName, ContentType = f.ContentType, SizeBytes = f.SizeBytes })],
                Checklists = [.. ChecklistJson.Read(v.ChecklistsJson).Select(Application.Instructions.ChecklistMapping.ToDto)],
            })],
            Targets = [.. targets.Select(t => t.ToDto())],
        };
    }

    public async Task<InstructionFileContent?> GetFileAsync(Guid instructionId, Guid fileId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<InstructionFileContent>(new CommandDefinition(
            """
            SELECT f.file_name, f.content_type, c.content
            FROM core.work_instruction_file f
            JOIN core.work_instruction_content c ON c.id = f.content_id
            JOIN core.work_instruction_version v ON v.id = f.version_id
            WHERE f.id = @FileId AND v.instruction_id = @InstructionId
            """, new { FileId = fileId, InstructionId = instructionId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<JobInstructionDto>> ForJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var matches = await connection.QueryAsync<(Guid, string, string)>(new CommandDefinition(
            InstructionSql.Matching + " SELECT id, code, kind FROM matched", new { JobId = jobId, LineId = (Guid?)null }, cancellationToken: cancellationToken));
        var shown = JobInstructions.Gather(matches);
        if (shown.Count == 0)
        {
            return [];
        }

        var ids = shown.Select(s => s.InstructionId).ToArray();
        var timeZone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT s.time_zone FROM core.job j JOIN core.equipment s ON s.id = j.site_id WHERE j.id = @JobId", new { JobId = jobId }, cancellationToken: cancellationToken));
        var versions = (await connection.QueryAsync<ReleasedRow>(new CommandDefinition(
            """
            SELECT i.id AS instruction_id, i.code, i.title, v.id AS version_id, v.version_number AS number, v.released_at, v.body,
                   v.checklists::text AS checklists_json
            FROM core.work_instruction i
            JOIN core.work_instruction_version v ON v.instruction_id = i.id AND v.status = 'released'
            WHERE i.id = ANY(@Ids)
            """, new { Ids = ids }, cancellationToken: cancellationToken))).ToDictionary(v => v.InstructionId);
        var files = (await connection.QueryAsync<FileRow>(new CommandDefinition(
            """
            SELECT f.id, f.version_id, f.file_name, f.content_type, c.size_bytes
            FROM core.work_instruction_file f
            JOIN core.work_instruction_content c ON c.id = f.content_id
            WHERE f.version_id = ANY(@Versions) AND f.removed_at IS NULL
            ORDER BY f.sort_order, f.id
            """, new { Versions = versions.Values.Select(v => v.VersionId).ToArray() }, cancellationToken: cancellationToken))).ToLookup(f => f.VersionId);

        // How each checklist stands for the job on its line: passed if one passed, else the latest answer (#126c).
        var answered = (await connection.QueryAsync<AnsweredRow>(new CommandDefinition(
            $"""
            SELECT DISTINCT ON (r.instruction_id, r.phase) r.instruction_id, r.phase, r.result, r.completed_at, r.answers::text AS answers_json,
                   {InstructionSql.UserName("r.completed_by")} AS completed_by_name
            FROM core.checklist_response r
            JOIN core.job j ON j.id = r.job_id AND j.equipment_id = r.equipment_id
            WHERE r.job_id = @JobId AND r.instruction_id = ANY(@Ids)
            ORDER BY r.instruction_id, r.phase, r.result = 'passed' DESC, r.completed_at DESC
            """, new { JobId = jobId, Ids = ids }, cancellationToken: cancellationToken))).ToDictionary(a => (a.InstructionId, a.Phase));
        return [.. shown.Where(s => versions.ContainsKey(s.InstructionId)).Select(s =>
        {
            var v = versions[s.InstructionId];
            return new JobInstructionDto
            {
                InstructionId = v.InstructionId,
                Code = v.Code,
                Title = v.Title,
                VersionId = v.VersionId,
                Number = v.Number,
                ReleasedAt = v.ReleasedAt,
                Body = v.Body,
                Files = [.. files[v.VersionId].Select(f => new InstructionFileDto { Id = f.Id, FileName = f.FileName, ContentType = f.ContentType, SizeBytes = f.SizeBytes })],
                ShownFor = s.Kinds,
                SiteTimeZone = timeZone,
                Checklists = [.. ChecklistJson.Read(v.ChecklistsJson).Select(c =>
                {
                    var dto = Application.Instructions.ChecklistMapping.ToDto(c);
                    return answered.TryGetValue((v.InstructionId, c.Phase), out var a)
                        ? new JobChecklistDto
                        {
                            Phase = c.Phase,
                            Items = dto.Items,
                            Result = a.Result,
                            CompletedAt = a.CompletedAt,
                            CompletedByName = a.CompletedByName,
                            Answers = [.. ChecklistJson.ReadAnswers(a.AnswersJson).Select(x => new ChecklistAnswerDto(x.Item, x.Value, x.Ok))],
                        }
                        : new JobChecklistDto { Phase = c.Phase, Items = dto.Items };
                })],
            };
        })];
    }

    private sealed class AnsweredRow
    {
        public Guid InstructionId { get; init; }
        public string Phase { get; init; } = "";
        public string Result { get; init; } = "";
        public DateTimeOffset CompletedAt { get; init; }
        public string AnswersJson { get; init; } = "[]";
        public string? CompletedByName { get; init; }
    }

    private sealed class VersionDtoRow
    {
        public Guid Id { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public string Body { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? ReleasedBy { get; init; }
        public string? ReleasedByName { get; init; }
        public string? Note { get; init; }
        public long Version { get; init; }
        public string ChecklistsJson { get; init; } = "[]";
    }

    private sealed class ReleasedRow
    {
        public Guid InstructionId { get; init; }
        public string Code { get; init; } = "";
        public string Title { get; init; } = "";
        public Guid VersionId { get; init; }
        public int Number { get; init; }
        public DateTimeOffset? ReleasedAt { get; init; }
        public string Body { get; init; } = "";
        public string ChecklistsJson { get; init; } = "[]";
    }

    private sealed class FileRow
    {
        public Guid Id { get; init; }
        public Guid VersionId { get; init; }
        public string FileName { get; init; } = "";
        public string ContentType { get; init; } = "";
        public int SizeBytes { get; init; }
    }

    private sealed class TargetRow
    {
        public Guid Id { get; init; }
        public Guid InstructionId { get; init; }
        public string Kind { get; init; } = "";
        public Guid? ProcessId { get; init; }
        public string? ProcessCode { get; init; }
        public int? OperationNumber { get; init; }
        public string? OperationName { get; init; }
        public Guid? MaterialId { get; init; }
        public string? MaterialCode { get; init; }
        public Guid? EquipmentId { get; init; }
        public string? EquipmentCode { get; init; }

        // Codes and numbers only, so a list needs no translating.
        public string Label() => Kind switch
        {
            InstructionTargetKinds.Operation => $"{ProcessCode} · {OperationNumber}",
            InstructionTargetKinds.Material => MaterialCode ?? "",
            _ => EquipmentCode ?? "",
        };

        public InstructionTargetDto ToDto() => new()
        {
            Id = Id,
            Kind = Kind,
            ProcessId = ProcessId,
            ProcessCode = ProcessCode,
            OperationNumber = OperationNumber,
            OperationName = OperationName,
            MaterialId = MaterialId,
            MaterialCode = MaterialCode,
            EquipmentId = EquipmentId,
            EquipmentCode = EquipmentCode,
        };
    }
}
