using Dapper;
using Npgsql;
using Pulaski.Application.WorkOrders;
using Pulaski.Infrastructure.Inventory;

namespace Pulaski.Infrastructure.WorkOrders;

/// <summary>What a job's production performance document needs beyond its execution (D-102).</summary>
internal sealed class ProductionPerformanceQueries(NpgsqlDataSource dataSource) : IProductionPerformanceReadStore
{
    private const string JobSql = """
        SELECT s.code AS site_code, wo.external_key, wo.external_system, o.operation_number
        FROM core.job j
        JOIN core.work_order wo ON wo.id = j.work_order_id
        JOIN core.equipment s ON s.id = wo.site_id
        LEFT JOIN core.process_operation o ON o.id = j.process_operation_id
        WHERE j.id = @Id
        """;

    // Each lot's standing produced and consumed declarations for the job, netted (a consumption put back counts against it).
    private const string LotsSql = $"""
        SELECT e.event_kind = 'produced' AS produced, m.code AS material_code, l.lot_number, loc.code AS location_code,
               sum(CASE WHEN e.event_kind = 'produced' THEN e.qty_delta ELSE -e.qty_delta END) AS quantity, bu.code AS uom_code
        FROM ts.lot_event e
        JOIN core.lot l ON l.id = e.lot_id
        JOIN core.material m ON m.id = l.material_id
        JOIN core.uom bu ON bu.id = m.base_uom_id
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        WHERE e.job_id = @Id AND e.event_kind IN ('produced', 'consumed') AND {LotEventSql.NotReversed}
        GROUP BY e.event_kind, l.id, m.code, l.lot_number, loc.code, bu.code
        HAVING sum(e.qty_delta) <> 0
        ORDER BY m.code, l.lot_number NULLS LAST, loc.code
        """;

    public async Task<JobPerformanceFacts?> GetJobFactsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = jobId };
        var job = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(JobSql, args, cancellationToken: cancellationToken));
        if (job is null)
        {
            return null;
        }

        var lots = await connection.QueryAsync<JobLotQuantity>(new CommandDefinition(LotsSql, args, cancellationToken: cancellationToken));
        return new JobPerformanceFacts
        {
            SiteCode = job.SiteCode,
            ExternalKey = job.ExternalKey,
            ExternalSystem = job.ExternalSystem,
            OperationNumber = job.OperationNumber,
            Lots = lots.AsList(),
        };
    }

    private sealed class JobRow
    {
        public string SiteCode { get; init; } = "";
        public string? ExternalKey { get; init; }
        public string? ExternalSystem { get; init; }
        public int? OperationNumber { get; init; }
    }
}
