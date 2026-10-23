using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>
/// One entry of a machine's definitions (D-050): a value of its state tag, or one of its stop, alarm or warning codes.
/// <see cref="Problems"/> holds what the source couldn't read; the row is then an error.
/// </summary>
public sealed record ProfileImportRow
{
    public int Line { get; init; }

    /// <summary><c>state</c>, <c>stop_reason</c>, <c>alarm</c> or <c>warning</c>.</summary>
    public string Kind { get; init; } = "";
    public int RawValue { get; init; }

    /// <summary>The state's name, or the code's message.</summary>
    public string Name { get; init; } = "";

    /// <summary>The PackML state a state value means; states only.</summary>
    public int? PackMlState { get; init; }
    public string? Category { get; init; }

    /// <summary>The code of the utilization reason it maps to; blank for none.</summary>
    public string? Reason { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>The rows from a source, and what was wrong with the source as a whole.</summary>
public sealed record ProfileImportSource(IReadOnlyList<ProfileImportRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// Reads a machine's definitions from CSV (D-050). The first line names the columns, in any order and case:
/// kind, value and name are required; packml_state is required on state rows; category and reason are optional.
/// </summary>
public static class MachineProfileCsv
{
    public const string State = "state";
    public const int MaxRows = 5000;

    /// <summary>The kinds a row may have.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [State, .. StopCodeKinds.All];

    /// <summary>The columns, by their canonical name, with the other names accepted for them.</summary>
    public static IReadOnlyDictionary<string, string[]> Columns { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["kind"] = ["type"],
        ["value"] = ["raw_value", "id", "number"],
        ["name"] = ["message", "text"],
        ["packml_state"] = ["packml", "state_current"],
        ["category"] = ["group"],
        ["reason"] = ["reason_code", "utilization_reason"],
    };

    private static readonly string[] Required = ["kind", "value", "name"];

    /// <summary>The header line of a file with every column, for a template.</summary>
    public static string Template { get; } = string.Join(",", Columns.Keys);

    public static ProfileImportSource Parse(string text)
    {
        var records = CsvText.Read(text);
        if (records.Count == 0)
        {
            return new ProfileImportSource([], ["The file is empty."]);
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

        problems.AddRange(Required.Where(r => !index.ContainsKey(r)).Select(r => $"The required column '{r}' is missing."));
        if (records.Count - 1 > MaxRows)
        {
            problems.Add($"The file has {records.Count - 1} rows; import at most {MaxRows} at a time.");
        }

        if (problems.Count > 0)
        {
            return new ProfileImportSource([], problems);
        }

        var rows = records.Skip(1)
            .Where(r => r.Fields.Any(f => f.Length > 0))
            .Select(r => ToRow(r, index))
            .ToList();
        return new ProfileImportSource(rows, rows.Count == 0 ? ["The file has a header but no rows."] : []);
    }

    private static ProfileImportRow ToRow(CsvRecord record, Dictionary<string, int> index)
    {
        var problems = new List<string>();
        string Cell(string column) =>
            index.TryGetValue(column, out var i) && i < record.Fields.Count ? record.Fields[i].Trim() : "";

        var kind = CsvText.Normalize(Cell("kind"));
        if (!Kinds.Contains(kind))
        {
            problems.Add($"kind: '{Cell("kind")}' isn't one of {string.Join(", ", Kinds)}.");
        }

        if (!int.TryParse(Cell("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            problems.Add($"value: '{Cell("value")}' isn't a whole number.");
        }

        int? packml = null;
        var packmlText = Cell("packml_state");
        if (kind != State && packmlText.Length > 0)
        {
            // Usually a column out of place: the rest of the row would land in the wrong fields.
            problems.Add($"packml_state: only a state row has one; is a column missing before '{packmlText}'?");
        }
        else if (kind == State)
        {
            packml = PackMlStates.Parse(packmlText);
            if (packml is null)
            {
                problems.Add(packmlText.Length == 0
                    ? "packml_state: a state row needs the PackML state it means."
                    : $"packml_state: '{packmlText}' isn't a PackML state (0 to 17, or a name such as Execute).");
            }
        }

        return new ProfileImportRow
        {
            Line = record.Line,
            Kind = kind,
            RawValue = value,
            Name = Cell("name"),
            PackMlState = packml,
            Category = Cell("category") is { Length: > 0 } category ? category : null,
            Reason = Cell("reason") is { Length: > 0 } reason ? reason.ToUpperInvariant() : null,
            Problems = problems,
        };
    }
}
