using System.Globalization;

namespace Pulaski.Application.Materials;

/// <summary>
/// The fields an import row is read from (D-034, D-053), by canonical name, and how their text becomes a <see cref="MaterialImportRow"/>.
/// Every source adapter goes through <see cref="ToRow"/>, so yes/no and whole-day values read the same from any source; the rules
/// stay in <see cref="ImportMaterialsCommand"/>.
/// </summary>
public static class MaterialImportFields
{
    /// <summary>The most rows one import takes, whatever its source.</summary>
    public const int MaxRows = 5000;

    /// <summary>The fields, by their canonical name, with the other names accepted for them.</summary>
    public static IReadOnlyDictionary<string, string[]> Names { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["code"] = ["item", "item_code", "material_code", "part_number"],
        ["name"] = ["material_name"],
        ["type"] = ["material_type"],
        ["base_unit"] = ["base_uom", "uom", "unit", "unit_of_measure"],
        ["class"] = ["material_class", "class_code"],
        ["description"] = [],
        ["lot_tracked"] = ["is_lot_tracked"],
        ["shelf_life_days"] = ["shelf_life"],
        ["active"] = ["is_active"],
        ["external_system"] = [],
        ["external_key"] = [],
        ["external_version"] = [],
    };

    /// <summary>The canonical name for <paramref name="name"/> (already normalized), or null when it names no field.</summary>
    public static string? Canonical(string name) =>
        Names.ContainsKey(name) ? name : Names.FirstOrDefault(c => c.Value.Contains(name)).Key;

    /// <summary>
    /// One row from its fields. <paramref name="field"/> gives a field's trimmed text by canonical name: null when the source
    /// doesn't supply it (the material keeps what it has), empty when it supplies a blank (an optional field is cleared).
    /// </summary>
    public static MaterialImportRow ToRow(int line, Func<string, string?> field, IEnumerable<string>? problems = null)
    {
        var rowProblems = new List<string>(problems ?? []);
        string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

        Supplied<bool>? Flag(string name)
        {
            if (field(name) is not { } value || value.Length == 0)
            {
                return null;
            }

            switch (value.ToLowerInvariant())
            {
                case "true" or "yes" or "y" or "1":
                    return new(true);
                case "false" or "no" or "n" or "0":
                    return new(false);
                default:
                    rowProblems.Add($"{name}: '{value}' isn't yes or no.");
                    return null;
            }
        }

        Supplied<int?>? shelfLife = null;
        if (field("shelf_life_days") is { } days)
        {
            if (days.Length == 0)
            {
                shelfLife = new(null);
            }
            else if (int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            {
                shelfLife = new(d);
            }
            else
            {
                rowProblems.Add($"shelf_life_days: '{days}' isn't a whole number of days.");
            }
        }

        return new MaterialImportRow
        {
            Line = line,
            Code = field("code") ?? "",
            Name = field("name") ?? "",
            MaterialType = (field("type") ?? "").ToLowerInvariant(),
            BaseUomCode = field("base_unit") ?? "",
            ExternalSystem = Blank(field("external_system")),
            ExternalKey = Blank(field("external_key")),
            ExternalVersion = Blank(field("external_version")),
            ClassCode = field("class") is { } c ? new(Blank(c)) : null,
            Description = field("description") is { } text ? new(Blank(text)) : null,
            IsLotTracked = Flag("lot_tracked"),
            ShelfLifeDays = shelfLife,
            IsActive = Flag("active"),
            Problems = rowProblems,
        };
    }
}
