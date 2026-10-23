using Dapper;
using Npgsql;
using Pulaski.Application.Scheduling;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Infrastructure.Scheduling;

/// <summary>Read side for planning rates from run history (D-045): the run standard and the line's ended runs of its material.</summary>
internal sealed class RunHistoryQueries(NpgsqlDataSource dataSource) : IRunHistoryReadStore
{
    private const string Standard = """
        SELECT rs.id, rs.equipment_id, rs.material_id, rs.uom_id, rs.calculated_rate_per_hour, rs.calculation_basis, rs.calculated_at,
               rs.modified_rate_per_hour, rs.modified_at, rs.modified_by, rs.modified_note, rs.xmin::text::bigint AS version,
               e.code AS line_code, m.code AS material_code, u.code AS uom_code,
               c.ideal_rate_per_hour, c.rate_uom_id AS ideal_uom_id
        FROM core.run_standard rs
        JOIN core.equipment e ON e.id = rs.equipment_id
        JOIN core.material m  ON m.id = rs.material_id
        JOIN core.uom u       ON u.id = rs.uom_id
        LEFT JOIN core.equipment_capability c ON c.equipment_id = rs.equipment_id AND c.material_id = rs.material_id
                                             AND c.effective_from <= @At AND (c.effective_to IS NULL OR c.effective_to > @At)
        WHERE rs.id = @Id
        """;

    // The run's own unit is the job's at the time it was reported; older runs may only have the job's.
    private const string Runs = """
        SELECT r.id AS run_id, j.job_number, r.run_no, r.started_at, r.ended_at, r.good_qty, coalesce(r.uom_id, j.uom_id) AS uom_id
        FROM core.job_run r
        JOIN core.job j ON j.id = r.job_id
        WHERE r.equipment_id = @LineId
          AND j.material_id = @MaterialId
          AND j.job_type = 'production'
          AND r.ended_at IS NOT NULL AND r.ended_at >= @From AND r.ended_at < @To
          AND coalesce(r.uom_id, j.uom_id) IS NOT NULL
        ORDER BY r.started_at
        """;

    public async Task<RunHistoryStandard?> GetStandardAsync(Guid runStandardId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var r = await connection.QuerySingleOrDefaultAsync<StandardRow>(new CommandDefinition(Standard, new { Id = runStandardId, At = at }, cancellationToken: cancellationToken));
        return r is null
            ? null
            : new RunHistoryStandard(
                new RunStandard(r.Id, r.EquipmentId, r.MaterialId, r.UomId, r.CalculatedRatePerHour, r.CalculationBasis, r.CalculatedAt,
                    r.ModifiedRatePerHour, r.ModifiedAt, r.ModifiedBy, r.ModifiedNote),
                r.Version, r.LineCode, r.MaterialCode, r.UomCode, r.IdealRatePerHour, r.IdealUomId);
    }

    public async Task<IReadOnlyList<RunHistoryRow>> ListRunsAsync(
        Guid lineId, Guid materialId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<RunRow>(new CommandDefinition(
                Runs, new { LineId = lineId, MaterialId = materialId, From = windowStart, To = windowEnd }, cancellationToken: cancellationToken)))
            .Select(r => new RunHistoryRow(r.RunId, r.JobNumber, r.RunNo, r.StartedAt, r.EndedAt, r.GoodQty, r.UomId))];
    }

    private sealed class StandardRow
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
        public string LineCode { get; init; } = "";
        public string MaterialCode { get; init; } = "";
        public string UomCode { get; init; } = "";
        public decimal? IdealRatePerHour { get; init; }
        public Guid? IdealUomId { get; init; }
    }

    private sealed class RunRow
    {
        public Guid RunId { get; init; }
        public string JobNumber { get; init; } = "";
        public int RunNo { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset EndedAt { get; init; }
        public decimal GoodQty { get; init; }
        public Guid UomId { get; init; }
    }
}
