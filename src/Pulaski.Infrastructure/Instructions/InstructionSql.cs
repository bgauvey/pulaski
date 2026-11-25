using System.Text.Json;
using Pulaski.Application.Instructions;
using Pulaski.Domain.Instructions;

namespace Pulaski.Infrastructure.Instructions;

/// <summary>SQL shared by instructions' reads and checklists' checks (#126b, #126c).</summary>
internal static class InstructionSql
{
    /// <summary>
    /// <c>matched</c>: the active instructions of the job's site with a released version and an active target matching the job (<c>@JobId</c>) on
    /// <c>@LineId</c> (null: the job's own equipment): its operation (process and number, whatever the version), the line or one above it, its
    /// material. One row per matching target: <c>id</c>, <c>code</c>, <c>kind</c>.
    /// </summary>
    public const string Matching = """
        WITH j AS (
            SELECT j.site_id, j.material_id, COALESCE(@LineId::uuid, j.equipment_id) AS line_id, v.process_id, o.operation_number
            FROM core.job j
            LEFT JOIN core.process_operation o ON o.id = j.process_operation_id
            LEFT JOIN core.process_version v ON v.id = o.process_version_id
            WHERE j.id = @JobId),
        matched AS (
            SELECT DISTINCT i.id, i.code, t.kind
            FROM j
            JOIN core.work_instruction i ON i.site_id = j.site_id AND i.is_active
            JOIN core.work_instruction_target t ON t.instruction_id = i.id AND t.is_active
            WHERE EXISTS (SELECT 1 FROM core.work_instruction_version r WHERE r.instruction_id = i.id AND r.status = 'released')
              AND ((t.kind = 'operation' AND t.process_id = j.process_id AND t.operation_number = j.operation_number)
                OR (t.kind = 'material' AND t.material_id = j.material_id)
                OR (t.kind = 'line' AND EXISTS (
                        SELECT 1 FROM core.equipment_closure c WHERE c.ancestor_id = t.equipment_id AND c.descendant_id = j.line_id))))
        """;

    /// <summary>The display name of the user an actor text (as events record it) names.</summary>
    public static string UserName(string actorColumn) =>
        $"(SELECT u.display_name FROM sec.app_user u WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = {actorColumn})";
}

/// <summary>Checklists and their answers as stored in <c>jsonb</c> (#126c): web-style names, as the rest of Pulaski's documents.</summary>
internal static class ChecklistJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write(IReadOnlyList<Checklist> checklists) => JsonSerializer.Serialize(checklists, Options);

    public static IReadOnlyList<Checklist> Read(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<Checklist>>(json, Options) ?? [];

    public static Checklist ReadOne(string json) => JsonSerializer.Deserialize<Checklist>(json, Options) ?? throw new JsonException("A checklist was empty.");

    public static string WriteAnswers(IReadOnlyList<ChecklistAnswer> answers) => JsonSerializer.Serialize(answers, Options);

    public static IReadOnlyList<ChecklistAnswer> ReadAnswers(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<ChecklistAnswer>>(json, Options) ?? [];
}

/// <summary>Checklists' writes and checks (#126c, D-116), in the command's transaction.</summary>
internal sealed class ChecklistRepository(Persistence.DbSession session) : IChecklistRepository
{
    public async Task<ChecklistJob?> LockJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ChecklistJob>(
            """
            SELECT j.id, j.site_id, j.job_number, j.status, j.equipment_id, e.can_run_job AS on_line
            FROM core.job j JOIN core.equipment e ON e.id = j.equipment_id
            WHERE j.id = @Id
            FOR UPDATE OF j
            """, new { Id = jobId }, cancellationToken);

    public async Task<IReadOnlyList<ApplicableChecklist>> ApplicableAsync(Guid jobId, Guid lineId, string phase, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync<(Guid, string, Guid, string)>(
            InstructionSql.Matching + """
                SELECT i.id, i.code, v.id, c.checklist::text
                FROM (SELECT DISTINCT id FROM matched) m
                JOIN core.work_instruction i ON i.id = m.id
                JOIN core.work_instruction_version v ON v.instruction_id = i.id AND v.status = 'released'
                CROSS JOIN LATERAL jsonb_path_query_first(v.checklists, '$[*] ? (@.phase == $p)', jsonb_build_object('p', @Phase::text)) AS c(checklist)
                WHERE c.checklist IS NOT NULL
                ORDER BY i.code
                """, new { JobId = jobId, LineId = lineId, Phase = phase }, cancellationToken);
        return [.. rows.Select(r => new ApplicableChecklist(r.Item1, r.Item2, r.Item3, ChecklistJson.ReadOne(r.Item4)))];
    }

    public async Task<IReadOnlyList<Guid>> PassedAsync(Guid jobId, Guid lineId, string phase, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<Guid>(
            """
            SELECT DISTINCT instruction_id FROM core.checklist_response
            WHERE job_id = @JobId AND equipment_id = @LineId AND phase = @Phase AND result = 'passed' AND (sign_off IS NULL OR sign_off = 'signed')
            """, new { JobId = jobId, LineId = lineId, Phase = phase }, cancellationToken)];

    public Task<Guid?> OpenRunAsync(Guid jobId, Guid lineId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM core.job_run WHERE job_id = @JobId AND equipment_id = @LineId AND ended_at IS NULL", new { JobId = jobId, LineId = lineId }, cancellationToken);

    public Task InsertAsync(ChecklistResponse response, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.checklist_response
                (id, site_id, job_id, equipment_id, run_id, instruction_id, version_id, phase, result, answers, completed_at, completed_by, sign_off)
            VALUES (@Id, @SiteId, @JobId, @EquipmentId, @RunId, @InstructionId, @VersionId, @Phase, @Result, @Answers::jsonb, @CompletedAt, @CompletedBy, @SignOff)
            """,
            new
            {
                response.Id,
                response.SiteId,
                response.JobId,
                response.EquipmentId,
                response.RunId,
                response.InstructionId,
                response.VersionId,
                response.Phase,
                Result = response.Result.Passed ? "passed" : "failed",
                Answers = ChecklistJson.WriteAnswers(response.Result.Answers),
                response.CompletedAt,
                response.CompletedBy,
                response.SignOff,
            }, cancellationToken);

    public Task<Guid?> GetResponseJobAsync(Guid responseId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>("SELECT job_id FROM core.checklist_response WHERE id = @Id", new { Id = responseId }, cancellationToken);

    public Task<ChecklistResponseState?> GetResponseForUpdateAsync(Guid responseId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<ChecklistResponseState?>(
            """
            SELECT r.id, r.job_id, j.job_number, i.code AS instruction_code, r.phase, r.sign_off, r.completed_by
            FROM core.checklist_response r
            JOIN core.job j ON j.id = r.job_id
            JOIN core.work_instruction i ON i.id = r.instruction_id
            WHERE r.id = @Id
            FOR UPDATE OF r
            """, new { Id = responseId }, cancellationToken);

    public Task SetSignOffAsync(Guid responseId, string signOff, string by, DateTimeOffset at, string? note, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "UPDATE core.checklist_response SET sign_off = @SignOff, sign_off_by = @By, sign_off_at = @At, sign_off_note = @Note WHERE id = @Id",
            new { Id = responseId, SignOff = signOff, By = by, At = at, Note = note }, cancellationToken);

    public Task AttachToRunAsync(Guid jobId, Guid lineId, Guid runId, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "UPDATE core.checklist_response SET run_id = @RunId WHERE job_id = @JobId AND equipment_id = @LineId AND run_id IS NULL",
            new { JobId = jobId, LineId = lineId, RunId = runId }, cancellationToken);
}
