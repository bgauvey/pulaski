using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Materials;

/// <summary>
/// Reads a materials CSV file into import rows (D-034). The first line names the columns, in any order
/// and case; a comma or semicolon separates fields, and quotes follow RFC 4180. Code, name, type and
/// base_unit are required; a column left out keeps what each material already has.
/// </summary>
public static class MaterialCsv
{
    public const int MaxRows = MaterialImportFields.MaxRows;

    /// <summary>The columns, by their canonical name, with the other names accepted for them.</summary>
    public static IReadOnlyDictionary<string, string[]> Columns => MaterialImportFields.Names;

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

            if (MaterialImportFields.Canonical(name) is not { } column)
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

    private static MaterialImportRow ToRow(CsvRecord record, Dictionary<string, int> index) =>
        MaterialImportFields.ToRow(record.Line, column =>
            index.TryGetValue(column, out var i) ? (i < record.Fields.Count ? record.Fields[i].Trim() : "") : null);
}
