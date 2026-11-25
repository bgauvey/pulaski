using Dapper;
using Npgsql;
using Pulaski.Application.Quality;
using Pulaski.Infrastructure.Processes;

namespace Pulaski.Infrastructure.Quality;

/// <summary>Due samples' reads (#136, D-121): open runs, released plans (from the version's definition), output and results.</summary>
internal sealed class SamplingQueries(NpgsqlDataSource dataSource) : ISamplingReadStore
{
    public async Task<IReadOnlyList<SamplingRun>> OpenRunsAsync(Guid? siteId, Guid? jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<SamplingRun>(new CommandDefinition(
            """
            SELECT r.id AS run_id, j.id AS job_id, j.job_number, r.equipment_id AS line_id, e.code AS line_code, j.material_id, r.started_at,
                   u.code AS uom_code, s.time_zone AS site_time_zone
            FROM core.job_run r
            JOIN core.job j ON j.id = r.job_id
            JOIN core.equipment e ON e.id = r.equipment_id
            JOIN core.equipment s ON s.id = j.site_id
            LEFT JOIN core.uom u ON u.id = j.uom_id
            WHERE r.ended_at IS NULL AND j.job_type = 'production' AND j.material_id IS NOT NULL
              AND (@SiteId::uuid IS NULL OR j.site_id = @SiteId) AND (@JobId::uuid IS NULL OR j.id = @JobId)
            ORDER BY e.code
            """, new { SiteId = siteId, JobId = jobId }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<SamplingItem>> PlansAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var versions = await connection.QueryAsync<(Guid MaterialId, string Definition)>(new CommandDefinition(
            """
            SELECT s.material_id, v.definition::text
            FROM core.material_spec s
            JOIN core.material_spec_version v ON v.spec_id = s.id AND v.status = 'released'
            WHERE s.is_active AND s.material_id = ANY(@Materials)
            """, new { Materials = materialIds.ToArray() }, cancellationToken: cancellationToken));
        var items = versions
            .SelectMany(v => ProcessJson.Read<SpecItemsDocument>(v.Definition).Items.Where(i => i.Sampling is { IsEmpty: false }).Select(i => (v.MaterialId, Item: i)))
            .ToList();
        if (items.Count == 0)
        {
            return [];
        }

        var names = (await connection.QueryAsync<(Guid Id, string Code, string Name, string DataKind, int SubgroupSize, string? Uom)>(new CommandDefinition(
            "SELECT c.id, c.code, c.name, c.data_kind, c.subgroup_size, u.code FROM core.quality_characteristic c LEFT JOIN core.uom u ON u.id = c.uom_id WHERE c.id = ANY(@Ids)",
            new { Ids = items.Select(i => i.Item.CharacteristicId).Distinct().ToArray() }, cancellationToken: cancellationToken))).ToDictionary(c => c.Id);
        return [.. items.Where(i => names.ContainsKey(i.Item.CharacteristicId)).Select(i =>
            new SamplingItem(i.MaterialId, i.Item.CharacteristicId, names[i.Item.CharacteristicId].Code, names[i.Item.CharacteristicId].Name, i.Item.Sampling!, i.Item.SortOrder)
            {
                DataKind = names[i.Item.CharacteristicId].DataKind,
                SubgroupSize = names[i.Item.CharacteristicId].SubgroupSize,
                UomCode = names[i.Item.CharacteristicId].Uom,
            })];
    }

    public async Task<IReadOnlyList<(Guid RunId, DateTimeOffset At, decimal Quantity)>> OutputAsync(
        IReadOnlyCollection<Guid> runIds, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<(Guid, DateTimeOffset, decimal)>(new CommandDefinition(
            """
            SELECT run_id, occurred_at, delta_qty
            FROM ts.production_count
            WHERE run_id = ANY(@Runs) AND occurred_at >= @Since AND count_kind = 'processed' AND source IN ('operator', 'correction')
            ORDER BY occurred_at
            """, new { Runs = runIds.ToArray(), Since = since }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<(Guid RunId, Guid CharacteristicId, DateTimeOffset At)>> SamplesAsync(
        IReadOnlyCollection<Guid> runIds, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<(Guid, Guid, DateTimeOffset)>(new CommandDefinition(
            """
            SELECT run_id, characteristic_id, occurred_at
            FROM ts.quality_sample
            WHERE run_id = ANY(@Runs) AND occurred_at >= @Since AND NOT is_excluded
            """, new { Runs = runIds.ToArray(), Since = since }, cancellationToken: cancellationToken))];
    }
}
