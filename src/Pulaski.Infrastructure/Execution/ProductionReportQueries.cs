using Dapper;
using Npgsql;
using Pulaski.Application.Execution;
using Pulaski.Application.Labor;
using Pulaski.Infrastructure.Labor;
using Pulaski.Infrastructure.Shifts;

namespace Pulaski.Infrastructure.Execution;

/// <summary>Read side for production by shift (D-044): the counts, the lines' working time and the runs' totals.</summary>
internal sealed class ProductionReportQueries(NpgsqlDataSource dataSource) : IProductionReportReadStore
{
    private const string TimeZone = "SELECT time_zone FROM core.equipment WHERE id = @Id AND level = 'site'";

    // Reported output, all of it and the scrapped part (D-035), with what the report shows of its line, job and material.
    private const string Counts = """
        SELECT c.equipment_id, e.code AS line_code, c.job_id, j.job_number, w.order_number AS work_order_number,
               m.code AS material_code, m.name AS material_name, c.uom_id, u.code AS uom_code,
               c.occurred_at, c.count_kind, c.delta_qty AS quantity
        FROM ts.production_count c
        JOIN core.equipment e ON e.id = c.equipment_id
        JOIN core.job j       ON j.id = c.job_id
        JOIN core.material m  ON m.id = j.material_id
        JOIN core.uom u       ON u.id = c.uom_id
        LEFT JOIN core.work_order w ON w.id = j.work_order_id
        WHERE e.site_id = @SiteId
          AND c.occurred_at >= @From AND c.occurred_at < @To
          AND c.count_kind IN ('processed', 'defective') AND c.source <> 'machine'
        """;

    private const string RunTotals = """
        SELECT job_id, sum(good_qty) AS good_qty, sum(scrap_qty) AS scrap_qty
        FROM core.job_run
        WHERE job_id = ANY(@Ids)
        GROUP BY job_id
        """;

    public async Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(TimeZone, new { Id = siteId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ProductionCountEntry>> ListCountsAsync(Guid siteId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<CountRow>(new CommandDefinition(Counts, new { SiteId = siteId, From = windowStart, To = windowEnd }, cancellationToken: cancellationToken)))
            .Select(r => new ProductionCountEntry(r.EquipmentId, r.LineCode, r.JobId, r.JobNumber, r.WorkOrderNumber, r.MaterialCode, r.MaterialName,
                r.UomId, r.UomCode, r.OccurredAt, r.CountKind, r.Quantity))];
    }

    public async Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<ShiftSql.CalendarRow>(new CommandDefinition(
                ShiftSql.EquipmentCalendars, new { Ids = equipmentIds.ToArray() }, cancellationToken: cancellationToken)))
            .Select(LaborSql.ToShifts)];
    }

    public async Task<IReadOnlyList<JobRunTotal>> ListRunTotalsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<TotalRow>(new CommandDefinition(RunTotals, new { Ids = jobIds.ToArray() }, cancellationToken: cancellationToken)))
            .Select(r => new JobRunTotal(r.JobId, r.GoodQty, r.ScrapQty))];
    }

    private sealed class CountRow
    {
        public Guid EquipmentId { get; init; }
        public string LineCode { get; init; } = "";
        public Guid JobId { get; init; }
        public string JobNumber { get; init; } = "";
        public string? WorkOrderNumber { get; init; }
        public string MaterialCode { get; init; } = "";
        public string MaterialName { get; init; } = "";
        public Guid UomId { get; init; }
        public string UomCode { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }
        public string CountKind { get; init; } = "";
        public decimal Quantity { get; init; }
    }

    private sealed class TotalRow
    {
        public Guid JobId { get; init; }
        public decimal GoodQty { get; init; }
        public decimal ScrapQty { get; init; }
    }
}
