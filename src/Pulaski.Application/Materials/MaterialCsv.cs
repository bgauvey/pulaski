using System.Globalization;
using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Materials;

/// <summary>
/// Reads a materials CSV file into import rows (D-034). The first line names the columns, in any order
/// and case; a comma or semicolon separates fields, and quotes follow RFC 4180. Code, name, type and
/// base_unit are required; a column left out keeps what each material already has.
/// </summary>
public static class MaterialCsv
{
    public const int MaxRows = 5000;

    /// <summary>The columns, by their canonical name, with the other names accepted for them.</summary>
    public static IReadOnlyDictionary<string, string[]> Columns { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["code"] = ["item", "item_code", "material_code", "part_number"],
        ["name"] = ["material_name"],
        ["type"] = ["material_type"],
        ["base_unit"] = ["base_uom", "uom", "unit"],
        ["class"] = ["material_class", "class_code"],
        ["description"] = [],
        ["lot_tracked"] = ["is_lot_tracked"],
        ["shelf_life_days"] = ["shelf_life"],
        ["active"] = ["is_active"],
        ["external_system"] = [],
        ["external_key"] = [],
        ["external_version"] = [],
    };

    private static readonly string[] Required = ["code", "name", "type", "base_unit"];

    /// <summary>The header line of a file with every column, for a template.</summary>
    public static string Template { get; } = string.Join(",", Columns.Keys);

    public static MaterialImportSource Parse(string text)
    {
        var records = CsvText.Read(text);
        if (records.Count == 0)
        {
            return new MaterialImportSource([], ["The file is empty."]);
        }

        var problems = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var header = records[0].Fields;
        for (var i = 0; i < header.Count; i++)
        {
            var name = CsvText.Normalize(header[i]);
            if (name.Length == 0)
            {
                continue;
            }

            if (Canonical(name) is not { } column)
            {
                problems.Add($"Unknown column '{header[i]}'. The columns are {string.Join(", ", Columns.Keys)}.");
            }
            else if (!index.TryAdd(column, i))
            {
                problems.Add($"Column '{column}' appears twice.");
            }
        }

        problems.AddRange(Required.Where(r => !index.ContainsKey(r)).Select(r => $"The required column '{r}' is missing."));
        if (records.Count - 1 > MaxRows)
        {
            problems.Add($"The file has {records.Count - 1} rows; import at most {MaxRows} at a time.");
        }

        if (problems.Count > 0)
        {
            return new MaterialImportSource([], problems);
        }

        var rows = records.Skip(1)
            .Where(r => r.Fields.Any(f => f.Length > 0))
            .Select(r => ToRow(r, index))
            .ToList();
        return new MaterialImportSource(rows, rows.Count == 0 ? ["The file has a header but no rows."] : []);
    }

    private static MaterialImportRow ToRow(CsvRecord record, Dictionary<string, int> index)
    {
        var rowProblems = new List<string>();
        string? Cell(string column) =>
            index.TryGetValue(column, out var i) ? (i < record.Fields.Count ? record.Fields[i].Trim() : "") : null;
        string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

        Supplied<bool>? Flag(string column)
        {
            if (Cell(column) is not { } value || value.Length == 0)
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
                    rowProblems.Add($"{column}: '{value}' isn't yes or no.");
                    return null;
            }
        }

        Supplied<int?>? shelfLife = null;
        if (Cell("shelf_life_days") is { } days)
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
            Line = record.Line,
            Code = Cell("code") ?? "",
            Name = Cell("name") ?? "",
            MaterialType = (Cell("type") ?? "").ToLowerInvariant(),
            BaseUomCode = Cell("base_unit") ?? "",
            ExternalSystem = Blank(Cell("external_system")),
            ExternalKey = Blank(Cell("external_key")),
            ExternalVersion = Blank(Cell("external_version")),
            ClassCode = Cell("class") is { } c ? new(Blank(c)) : null,
            Description = Cell("description") is { } text ? new(Blank(text)) : null,
            IsLotTracked = Flag("lot_tracked"),
            ShelfLifeDays = shelfLife,
            IsActive = Flag("active"),
            Problems = rowProblems,
        };
    }

    private static string? Canonical(string name) =>
        Columns.ContainsKey(name) ? name : Columns.FirstOrDefault(c => c.Value.Contains(name)).Key;
}
