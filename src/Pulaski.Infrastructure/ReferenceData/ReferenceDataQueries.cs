using Dapper;
using Npgsql;
using Pulaski.Application.ReferenceData;
using Pulaski.Contracts.ReferenceData;

namespace Pulaski.Infrastructure.ReferenceData;

/// <summary>Read side for units and materials.</summary>
internal sealed class ReferenceDataQueries(NpgsqlDataSource dataSource) : IReferenceDataReadStore
{
    // Base code: the dimension's unit with factor 1 (D-026); packaging units have none.
    private const string Uoms = """
        SELECT u.id, u.code, u.name, u.dimension, u.to_base_factor, u.is_active, u.xmin::text::bigint AS version,
               (SELECT b.code FROM core.uom b
                WHERE b.dimension = u.dimension AND b.to_base_factor = 1
                ORDER BY b.is_active DESC, b.code LIMIT 1) AS base_code
        FROM core.uom u
        ORDER BY u.dimension, u.to_base_factor NULLS LAST, u.code
        """;

    private const string Materials = $"""
        {Pulaski.Infrastructure.Materials.MaterialSql.Select}
        WHERE @IncludeInactive OR m.is_active
        ORDER BY m.code
        """;

    private const string MaterialClasses = """
        SELECT c.id, c.code, c.name, c.is_active, c.xmin::text::bigint AS version,
               (SELECT count(*)::int FROM core.material m WHERE m.material_class_id = c.id) AS material_count
        FROM core.material_class c
        ORDER BY c.code
        """;

    public async Task<IReadOnlyList<MaterialClassDto>> ListMaterialClassesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialClassDto>(new CommandDefinition(MaterialClasses, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<UomDto>> ListUomsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<UomDto>(new CommandDefinition(Uoms, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<MaterialDto>> ListMaterialsAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialDto>(
            new CommandDefinition(Materials, new { IncludeInactive = includeInactive }, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
