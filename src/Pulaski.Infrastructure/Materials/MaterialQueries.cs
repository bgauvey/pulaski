using Dapper;
using Npgsql;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;
using Pulaski.Contracts.ReferenceData;

namespace Pulaski.Infrastructure.Materials;

/// <summary>Read side for the material page (D-033).</summary>
internal sealed class MaterialQueries(NpgsqlDataSource dataSource) : IMaterialReadStore
{
    private const string MaterialById = $"""
        {MaterialSql.Select}
        WHERE m.id = @Id
        """;

    private const string BaseUomName = "SELECT u.name FROM core.material m JOIN core.uom u ON u.id = m.base_uom_id WHERE m.id = @Id";

    private const string Capabilities = """
        SELECT c.id, c.equipment_id, e.code AS equipment_code, e.name AS equipment_name, site.time_zone AS site_time_zone,
               c.ideal_rate_per_hour, c.rate_uom_id, u.code AS rate_uom_code,
               c.standard_changeover, c.standard_crew_size, c.is_preferred, c.effective_from, c.effective_to, c.batch_size,
               (c.effective_from <= now() AND (c.effective_to IS NULL OR c.effective_to > now())) AS is_in_effect,
               c.xmin::text::bigint AS version,
               rs.id AS run_standard_id, rs.xmin::text::bigint AS run_standard_version, rs.calculated_rate_per_hour, rs.calculation_basis,
               rs.modified_rate_per_hour, rs.modified_by, rs.modified_at, rs.modified_note,
               rs.effective_rate_per_hour, rs.rate_source, ru.code AS run_standard_uom_code
        FROM core.equipment_capability c
        JOIN core.equipment e ON e.id = c.equipment_id
        LEFT JOIN core.equipment site ON site.id = e.site_id
        JOIN core.uom u ON u.id = c.rate_uom_id
        LEFT JOIN core.run_standard rs ON rs.equipment_id = c.equipment_id AND rs.material_id = c.material_id
        LEFT JOIN core.uom ru ON ru.id = rs.uom_id
        WHERE c.material_id = @Id
        ORDER BY e.code, c.effective_from
        """;

    public async Task<MaterialDetailDto?> GetDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = id };
        var material = await connection.QuerySingleOrDefaultAsync<MaterialDto>(new CommandDefinition(MaterialById, args, cancellationToken: cancellationToken));
        if (material is null)
        {
            return null;
        }

        var unitName = await connection.ExecuteScalarAsync<string>(new CommandDefinition(BaseUomName, args, cancellationToken: cancellationToken));
        var usage = await connection.QuerySingleAsync<MaterialUsageDto>(new CommandDefinition(MaterialSql.Usage, args, cancellationToken: cancellationToken));
        var inUse = await connection.QuerySingleAsync<bool>(new CommandDefinition(
            "SELECT in_use FROM (" + MaterialSql.Use + ") x", args, cancellationToken: cancellationToken));
        var capabilities = await connection.QueryAsync<MaterialCapabilityDto>(new CommandDefinition(Capabilities, args, cancellationToken: cancellationToken));
        return new MaterialDetailDto
        {
            Material = material,
            BaseUomName = unitName ?? "",
            Usage = usage with { InUse = inUse },
            Capabilities = capabilities.AsList(),
        };
    }
}
