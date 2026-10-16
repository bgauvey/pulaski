using Dapper;
using Npgsql;
using Pulaski.Application.Units;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Domain.Units;

namespace Pulaski.Infrastructure.Units;

/// <summary>Read side for material conversions and the unit catalog used to convert (D-026).</summary>
internal sealed class UnitQueries(NpgsqlDataSource dataSource) : IUnitReadStore
{
    private const string ListConversions = """
        SELECT c.material_id, c.from_uom_id, f.code AS from_uom_code, c.to_uom_id, t.code AS to_uom_code, c.factor, c.updated_at
        FROM core.material_uom_conversion c
        JOIN core.uom f ON f.id = c.from_uom_id
        JOIN core.uom t ON t.id = c.to_uom_id
        WHERE c.material_id = @MaterialId AND c.is_active
        ORDER BY f.code, t.code
        """;

    public async Task<IReadOnlyList<MaterialConversionDto>> ListConversionsAsync(Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialConversionDto>(
            new CommandDefinition(ListConversions, new { MaterialId = materialId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Unit>> GetUnitsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<UnitRow>(new CommandDefinition(UnitSql.Units, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<MaterialConversionRow>> GetConversionsAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialConversionRow>(
            new CommandDefinition(UnitSql.Conversions, new { MaterialIds = materialIds.ToArray() }, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}

/// <summary>Column shape of a unit for converting.</summary>
internal sealed class UnitRow
{
    public Guid Id { get; init; }
    public string Code { get; init; } = "";
    public string Dimension { get; init; } = "";
    public decimal? ToBaseFactor { get; init; }

    public Unit ToDomain() => new(Id, Code, Dimension, ToBaseFactor);
}
