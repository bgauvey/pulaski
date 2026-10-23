using System.Text;

namespace Pulaski.Application.Abstractions;

/// <summary>One record of a CSV file and the line it starts on.</summary>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

/// <summary>Reads CSV files for the imports (D-034, D-050): a comma or semicolon separates fields, quotes follow RFC 4180.</summary>
public static class CsvText
{
    /// <summary>A header as a column name: trimmed, lower case, spaces and dashes as '_'.</summary>
    public static string Normalize(string header) =>
        header.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    /// <summary>RFC 4180 records: quoted fields may hold separators, doubled quotes and line breaks.</summary>
    public static IReadOnlyList<CsvRecord> Read(string text)
    {
        text = text.TrimStart('\uFEFF');
        var firstLine = text.Split('\n', 2)[0];
        var separator = firstLine.Count(ch => ch == ';') > firstLine.Count(ch => ch == ',') ? ';' : ',';

        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    line += ch == '\n' ? 1 : 0;
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    records.Add(new CsvRecord(recordLine, [.. fields]));
                    fields.Clear();
                    field.Clear();
                    line++;
                    recordLine = line;
                    break;
                default:
                    if (ch == separator)
                    {
                        fields.Add(field.ToString());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(ch);
                    }

                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordLine, [.. fields]));
        }

        return records;
    }
}
