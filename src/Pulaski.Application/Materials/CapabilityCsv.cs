using System.Globalization;
using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Materials;

/// <summary>One material on one line from a capabilities file (D-055), before any rule is applied.</summary>
public sealed record CapabilityImportRow
{
    public int Line { get; init; }
    public string MaterialCode { get; init; } = "";

    /// <summary>The line's site code, needed only when the line code is used at more than one site.</summary>
    public string? SiteCode { get; init; }
    public string LineCode { get; init; } = "";
    public decimal? IdealRatePerHour { get; init; }
    public string RateUnit { get; init; } = "";
    public Supplied<int?>? ChangeoverMinutes { get; init; }
    public Supplied<decimal?>? CrewSize { get; init; }
    public Supplied<bool>? IsPreferred { get; init; }

    /// <summary>The line's own batch size (D-065); blank for the material's.</summary>
    public Supplied<decimal?>? BatchSize { get; init; }

    /// <summary>The line's own batch tolerance in percent (#85); blank for the material's.</summary>
    public Supplied<decimal?>? BatchTolerancePct { get; init; }

    /// <summary>False ends the capability in effect now; true or left out keeps or adds one.</summary>
    public Supplied<bool>? IsActive { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public sealed record CapabilityImportSource(IReadOnlyList<CapabilityImportRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// Reads a capabilities CSV file (D-055): one row per material and line, columns named in the first line in any order and case.
/// Material, line, ideal_rate and rate_unit are required columns (the rate may be blank on a row that ends a capability); a column
/// left out keeps what the capability has.
/// </summary>
public static class CapabilityCsv
{
    public const int MaxRows = 5000;

    /// <summary>The columns, by their canonical name, with the other names accepted for them.</summary>
    public static IReadOnlyDictionary<string, string[]> Columns { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["material"] = ["code", "material_code", "item", "item_code"],
        ["site"] = ["site_code"],
        ["line"] = ["line_code", "equipment", "equipment_code"],
        ["ideal_rate"] = ["rate", "ideal_rate_per_hour"],
        ["rate_unit"] = ["rate_uom", "unit", "uom"],
        ["changeover_minutes"] = ["changeover", "standard_changeover_minutes"],
        ["crew_size"] = ["crew", "standard_crew_size"],
        ["preferred"] = ["is_preferred"],
        ["batch_size"] = ["line_batch_size"],
        ["batch_tolerance"] = ["batch_tolerance_pct", "line_batch_tolerance"],
        ["active"] = ["is_active"],
    };

    private static readonly string[] Required = ["material", "line", "ideal_rate", "rate_unit"];

    /// <summary>The header line of a file with every column, for a template.</summary>
    public static string Template { get; } = string.Join(",", Columns.Keys);

    public static CapabilityImportSource Parse(string text)
    {
        var records = CsvText.Read(text);
        if (records.Count == 0)
        {
            return new CapabilityImportSource([], ["The file is empty."]);
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
            return new CapabilityImportSource([], problems);
        }

        var rows = records.Skip(1).Where(r => r.Fields.Any(f => f.Length > 0)).Select(r => ToRow(r, index)).ToList();
        return new CapabilityImportSource(rows, rows.Count == 0 ? ["The file has a header but no rows."] : []);
    }

    private static CapabilityImportRow ToRow(CsvRecord record, Dictionary<string, int> index)
    {
        var problems = new List<string>();
        string? Cell(string column) =>
            index.TryGetValue(column, out var i) ? (i < record.Fields.Count ? record.Fields[i].Trim() : "") : null;

        Supplied<bool>? Flag(string column)
        {
            if (Cell(column) is not { Length: > 0 } value)
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
                    problems.Add($"{column}: '{value}' isn't yes or no.");
                    return null;
            }
        }

        decimal? Number(string? value, string column)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            // A decimal point only: "1,5" from a spreadsheet in another language must not be read as 15.
            if (decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            problems.Add($"{column}: '{value}' isn't a number; use a decimal point and no thousands separator.");
            return null;
        }

        Supplied<int?>? changeover = null;
        if (Cell("changeover_minutes") is { } minutes)
        {
            if (minutes.Length == 0)
            {
                changeover = new(null);
            }
            else if (int.TryParse(minutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            {
                changeover = new(m);
            }
            else
            {
                problems.Add($"changeover_minutes: '{minutes}' isn't a whole number of minutes.");
            }
        }

        return new CapabilityImportRow
        {
            Line = record.Line,
            MaterialCode = Cell("material") ?? "",
            SiteCode = Cell("site") is { Length: > 0 } site ? site : null,
            LineCode = Cell("line") ?? "",
            IdealRatePerHour = Number(Cell("ideal_rate"), "ideal_rate"),
            RateUnit = Cell("rate_unit") ?? "",
            ChangeoverMinutes = changeover,
            CrewSize = Cell("crew_size") is { } crew ? new(Number(crew, "crew_size")) : null,
            IsPreferred = Flag("preferred"),
            BatchSize = Cell("batch_size") is { } batch ? new(batch.Length == 0 ? null : Number(batch, "batch_size")) : null,
            BatchTolerancePct = Cell("batch_tolerance") is { } tolerance ? new(tolerance.Length == 0 ? null : Number(tolerance, "batch_tolerance")) : null,
            IsActive = Flag("active"),
            Problems = problems,
        };
    }
}
