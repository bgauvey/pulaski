using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Labor;

/// <summary>
/// Reads a people CSV file into import rows (D-054), as an HR or ERP report exports it. The first line names the columns, in any
/// order and case; a comma or semicolon separates fields, and quotes follow RFC 4180. Employee number and name are required; a
/// column left out keeps what each person already has. An external system given on import is the system of every row that has an
/// <c>external_key</c> but no <c>external_system</c> of its own.
/// </summary>
public static class PeopleCsv
{
    private static readonly string[] Required = ["employee_number", "name"];

    /// <summary>The header line of a file with every column, for a template.</summary>
    public static string Template { get; } = string.Join(",", PersonImportFields.Names.Keys);

    public static PersonImportSource Parse(string text, string? externalSystem = null)
    {
        var records = CsvText.Read(text);
        if (records.Count == 0)
        {
            return new PersonImportSource([], ["The file is empty."]);
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

            if (PersonImportFields.Canonical(name) is not { } column)
            {
                problems.Add($"Unknown column '{header[i]}'. The columns are {string.Join(", ", PersonImportFields.Names.Keys)}.");
            }
            else if (!index.TryAdd(column, i))
            {
                problems.Add($"Column '{column}' appears twice.");
            }
        }

        problems.AddRange(Required.Where(r => !index.ContainsKey(r)).Select(r => $"The required column '{r}' is missing."));
        if (records.Count - 1 > PersonImportFields.MaxRows)
        {
            problems.Add($"The file has {records.Count - 1} rows; import at most {PersonImportFields.MaxRows} at a time.");
        }

        if (problems.Count > 0)
        {
            return new PersonImportSource([], problems);
        }

        var system = string.IsNullOrWhiteSpace(externalSystem) ? null : externalSystem.Trim();
        var rows = records.Skip(1)
            .Where(r => r.Fields.Any(f => f.Length > 0))
            .Select(r => ToRow(r, index, system))
            .ToList();
        return new PersonImportSource(rows, rows.Count == 0 ? ["The file has a header but no rows."] : []);
    }

    private static PersonImportRow ToRow(CsvRecord record, Dictionary<string, int> index, string? system)
    {
        string? Cell(string column) =>
            index.TryGetValue(column, out var i) ? (i < record.Fields.Count ? record.Fields[i].Trim() : "") : null;

        return PersonImportFields.ToRow(record.Line, column =>
            column == "external_system" && system is not null && string.IsNullOrEmpty(Cell(column)) && !string.IsNullOrEmpty(Cell("external_key"))
                ? system
                : Cell(column));
    }
}
