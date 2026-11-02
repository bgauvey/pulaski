using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Inventory;

/// <summary>One lot's row of an attribute values file (#78): the lot by number (and material, when numbers repeat), and a cell per attribute.</summary>
public sealed record LotAttributeImportRow(int Line, string LotNumber, string MaterialCode, IReadOnlyDictionary<string, string> Cells);

public sealed record LotAttributeImportSource(IReadOnlyList<LotAttributeImportRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// Lot attribute values as CSV (#78): <c>lot_number</c>, optional <c>material_code</c>, then one column per attribute code of the site, in
/// any order and case. Values are read as on the lot page (D-056). A blank cell leaves the value as it is; <see cref="Clear"/> clears it.
/// </summary>
public static class LotAttributeCsv
{
    public const int MaxRows = 10_000;
    public const string LotColumn = "lot_number";
    public const string MaterialColumn = "material_code";

    /// <summary>What a cell holds to clear the value, since a blank cell leaves it alone.</summary>
    public const string Clear = "(clear)";

    private static readonly string[] LotAliases = [LotColumn, "lot", "lot_no"];
    private static readonly string[] MaterialAliases = [MaterialColumn, "material", "item"];

    /// <summary>The header line for the site's attributes, for a template.</summary>
    public static string Template(IEnumerable<string> attributeCodes) => CsvText.Write([LotColumn, MaterialColumn, .. attributeCodes], []);

    /// <summary>Reads the file; <paramref name="attributeCodes"/> are the codes of the site's attributes, which the other columns must name.</summary>
    public static LotAttributeImportSource Parse(string text, IReadOnlyCollection<string> attributeCodes)
    {
        var records = CsvText.Read(text);
        if (records.Count == 0)
        {
            return new([], ["The file is empty."]);
        }

        var byName = attributeCodes.ToDictionary(CsvText.Normalize, c => c, StringComparer.Ordinal);
        var problems = new List<string>();
        int? lot = null;
        int? material = null;
        var columns = new Dictionary<int, string>();
        var header = records[0].Fields;
        for (var i = 0; i < header.Count; i++)
        {
            var name = CsvText.Normalize(header[i]);
            if (name.Length == 0)
            {
                continue;
            }

            if (LotAliases.Contains(name))
            {
                problems.AddRange(lot is null ? [] : ["Column 'lot_number' appears twice."]);
                lot ??= i;
            }
            else if (MaterialAliases.Contains(name))
            {
                problems.AddRange(material is null ? [] : ["Column 'material_code' appears twice."]);
                material ??= i;
            }
            else if (byName.TryGetValue(name, out var code))
            {
                if (columns.ContainsValue(code))
                {
                    problems.Add($"Column '{code}' appears twice.");
                }

                columns[i] = code;
            }
            else
            {
                problems.Add($"'{header[i]}' isn't one of the site's lot attributes ({string.Join(", ", attributeCodes)}).");
            }
        }

        if (lot is null)
        {
            problems.Add("The required column 'lot_number' is missing.");
        }

        if (columns.Count == 0)
        {
            problems.Add("Name at least one lot attribute in the header.");
        }

        if (records.Count - 1 > MaxRows)
        {
            problems.Add($"The file has {records.Count - 1} rows; import at most {MaxRows} at a time.");
        }

        if (problems.Count > 0)
        {
            return new([], problems);
        }

        string Cell(CsvRecord r, int? i) => i is { } at && at < r.Fields.Count ? r.Fields[at].Trim() : "";
        var rows = records.Skip(1)
            .Where(r => r.Fields.Any(f => f.Length > 0))
            .Select(r => new LotAttributeImportRow(
                r.Line,
                Cell(r, lot),
                Cell(r, material),
                columns.Select(c => (c.Value, Text: Cell(r, c.Key))).Where(c => c.Text.Length > 0).ToDictionary(c => c.Value, c => c.Text)))
            .ToList();
        return new(rows, rows.Count == 0 ? ["The file has a header but no rows."] : []);
    }
}
