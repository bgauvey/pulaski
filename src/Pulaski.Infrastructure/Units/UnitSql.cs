namespace Pulaski.Infrastructure.Units;

/// <summary>SQL shared by the unit read store and repository (D-026).</summary>
internal static class UnitSql
{
    public const string Units = "SELECT id, code, dimension, to_base_factor FROM core.uom";

    public const string Conversions = """
        SELECT material_id, from_uom_id, to_uom_id, factor
        FROM core.material_uom_conversion
        WHERE material_id = ANY(@MaterialIds) AND is_active
        """;
}
