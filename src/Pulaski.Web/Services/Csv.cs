using System.Globalization;
using System.Text;

namespace Pulaski.Web.Services;

/// <summary>
/// Writes CSV for spreadsheet exports (RFC 4180): fields quoted when needed, CRLF line ends, numbers
/// and times culture-invariant so every spreadsheet reads them the same way.
/// </summary>
public static class Csv
{
    public static string Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var text = new StringBuilder();
        Line(text, headers);
        foreach (var row in rows)
        {
            Line(text, row);
        }

        return text.ToString();
    }

    /// <summary>A file name with the time it was made: <c>stock-PLT1-20261016-1405.csv</c>.</summary>
    public static string FileName(string what, string? site, DateTimeOffset at) =>
        string.Join('-', new[] { what, site, at.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) }.Where(p => !string.IsNullOrEmpty(p))) + ".csv";

    private static void Line(StringBuilder text, IEnumerable<object?> fields)
    {
        text.AppendJoin(',', fields.Select(Field));
        text.Append("\r\n");
    }

    private static string Field(object? value)
    {
        var raw = value switch
        {
            null => "",
            DateTimeOffset at => at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            decimal d => d.ToString("0.######", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        // Text starting with =, +, - or @ would run as a formula in a spreadsheet; a quote mark keeps it text.
        if (value is string && raw.Length > 0 && raw[0] is '=' or '+' or '-' or '@')
        {
            raw = "'" + raw;
        }

        return raw.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + raw.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : raw;
    }
}
