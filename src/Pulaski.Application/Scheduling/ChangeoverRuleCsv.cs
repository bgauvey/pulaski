using System.Globalization;
using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Scheduling;

/// <summary>One changeover rule from a file (D-074), by codes, before any check against the site.</summary>
public sealed record ChangeoverRuleImportRow
{
    public int Line { get; init; }

    /// <summary>The line's code; blank for every line of the site.</summary>
    public string LineCode { get; init; } = "";
    public string FromMaterial { get; init; } = "";
    public string FromClass { get; init; } = "";
    public string ToMaterial { get; init; } = "";
    public string ToClass { get; init; } = "";
    public int? Minutes { get; init; }

    /// <summary>Left out (no column), the note stays as it is; a blank cell clears it.</summary>
    public Supplied<string?>? Note { get; init; }

    /// <summary>False switches the rule off; left out or true, it is on.</summary>
    public bool? IsActive { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>"From → to on line", for messages.</summary>
    public string Label =>
        $"{Side(FromMaterial, FromClass)} → {Side(ToMaterial, ToClass)}{(LineCode.Length > 0 ? $" on {LineCode}" : "")}";

    private static string Side(string material, string materialClass) =>
        material.Length > 0 ? material : materialClass.Length > 0 ? $"class {materialClass}" : "anything";
}

public sealed record ChangeoverRuleImportSource(IReadOnlyList<ChangeoverRuleImportRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// Reads a changeover rules CSV file (D-074): one rule per row, columns named in the first line in any order and case. A row matches the
/// site's rule with the same line and sides, which it changes; else it adds one. <c>minutes</c> is required on every row.
/// </summary>
public static class ChangeoverRuleCsv
{
    public const int MaxRows = 5000;

    /// <summary>The columns, by their canonical name, with the other names accepted for them.</summary>
    public static IReadOnlyDictionary<string, string[]> Columns { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["line"] = ["line_code", "equipment", "equipment_code"],
        ["from_material"] = ["from", "from_item"],
        ["from_class"] = ["from_material_class"],
        ["to_material"] = ["to", "to_item"],
        ["to_class"] = ["to_material_class"],
        ["minutes"] = ["duration_minutes", "changeover_minutes", "duration"],
        ["note"] = ["comment"],
        ["active"] = ["is_active"],
    };

    /// <summary>The header line of a file with every column, for a template.</summary>
    public static string Template { get; } = string.Join(",", Columns.Keys);

    public static ChangeoverRuleImportSource Parse(string text)
    {
        var records = CsvText.Read(text);
        if (records.Count == 0)
        {
            return new([], ["The file is empty."]);
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

            var column = Columns.ContainsKey(name) ? name : Columns.FirstOrDefault(c => c.Value.Contains(name)).Key;
            if (column is null)
            {
                problems.Add($"Unknown column '{header[i]}'. The columns are {string.Join(", ", Columns.Keys)}.");
            }
            else if (!index.TryAdd(column, i))
            {
                problems.Add($"Column '{column}' appears twice.");
            }
        }

        if (!index.ContainsKey("minutes"))
        {
            problems.Add("The required column 'minutes' is missing.");
        }

        if (records.Count - 1 > MaxRows)
        {
            problems.Add($"The file has {records.Count - 1} rows; import at most {MaxRows} at a time.");
        }

        if (problems.Count > 0)
        {
            return new([], problems);
        }

        var rows = records.Skip(1).Where(r => r.Fields.Any(f => f.Length > 0)).Select(r => ToRow(r, index)).ToList();
        return new(rows, rows.Count == 0 ? ["The file has a header but no rows."] : []);
    }

    private static ChangeoverRuleImportRow ToRow(CsvRecord record, Dictionary<string, int> index)
    {
        var problems = new List<string>();
        string? Cell(string column) =>
            index.TryGetValue(column, out var i) ? (i < record.Fields.Count ? record.Fields[i].Trim() : "") : null;

        int? minutes = null;
        if (Cell("minutes") is not { Length: > 0 } text)
        {
            problems.Add("minutes: required.");
        }
        else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
        {
            minutes = m;
        }
        else
        {
            problems.Add($"minutes: '{text}' isn't a whole number of minutes.");
        }

        bool? active = null;
        if (Cell("active") is { Length: > 0 } flag)
        {
            active = flag.ToLowerInvariant() switch
            {
                "true" or "yes" or "y" or "1" => true,
                "false" or "no" or "n" or "0" => false,
                _ => null,
            };
            if (active is null)
            {
                problems.Add($"active: '{flag}' isn't yes or no.");
            }
        }

        return new ChangeoverRuleImportRow
        {
            Line = record.Line,
            LineCode = Cell("line") ?? "",
            FromMaterial = Cell("from_material") ?? "",
            FromClass = Cell("from_class") ?? "",
            ToMaterial = Cell("to_material") ?? "",
            ToClass = Cell("to_class") ?? "",
            Minutes = minutes,
            Note = Cell("note") is { } note ? new(note.Length == 0 ? null : note) : null,
            IsActive = active,
            Problems = problems,
        };
    }
}
