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

    private const string Materials = """
        SELECT m.id, m.code, m.name, m.material_type, m.base_uom_id, u.code AS base_uom_code,
               m.material_class_id, c.code AS material_class_code, m.is_lot_tracked, m.is_active
        FROM core.material m
        JOIN core.uom u ON u.id = m.base_uom_id
        LEFT JOIN core.material_class c ON c.id = m.material_class_id
        WHERE @IncludeInactive OR m.is_active
        ORDER BY m.code
        """;

    private const string MaterialClasses = "SELECT id, code, name FROM core.material_class ORDER BY code";

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
