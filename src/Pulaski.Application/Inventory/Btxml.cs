using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Pulaski.Application.Inventory;

/// <summary>
/// BarTender's BTXML Script for a printer of method <c>bartender</c> (#168, D-110). Pure. A template is a whole <c>XMLScript</c> with
/// placeholders: <c>{{BtwPath}}</c> (the .btw document for the operation), <c>{{NamedSubStrings}}</c> (every label value as a
/// <c>NamedSubString</c>), <c>{{PrintSetup}}</c> (the printer, when set, and the copies), <c>{{Printer}}</c>, <c>{{Copies}}</c>,
/// <c>{{JobName}}</c>, and any label value by name (<c>{{Code}}</c>, <c>{{Title}}</c>, <c>{{Line1}}</c> …), all escaped for XML.
/// </summary>
public static partial class Btxml
{
    public const int MaxTemplate = 20000;
    public const int MaxPath = 260;

    /// <summary>Operations a printer names .btw documents for; the most specific one set is used, else <see cref="Default"/>.</summary>
    public const string Default = "default";

    public static IReadOnlyList<string> Operations { get; } = [Default, "lot", "container", "location"];

    public const string StandardTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <XMLScript Version="2.0" Name="{{JobName}}">
          <Command Name="{{JobName}}">
            <Print>
              <Format>{{BtwPath}}</Format>
              {{NamedSubStrings}}
              {{PrintSetup}}
            </Print>
          </Command>
        </XMLScript>
        """;

    // Values that only mean something in ZPL, or carry the GS1 group separator (not allowed in XML): BarTender reads Gs1Hri instead.
    private static readonly HashSet<string> NotSent = ["Gs1Data", "Gs1Zpl128", "Gs1ZplDataMatrix"];

    /// <summary>
    /// The .btw document for <paramref name="operation"/>: its own, else that of the kind before its first dot (<c>lot</c> for
    /// <c>lot.produced</c>), else the default; null when none is set.
    /// </summary>
    public static string? BtwFor(IReadOnlyDictionary<string, string> files, string operation)
    {
        if (files.TryGetValue(operation, out var own))
        {
            return own;
        }

        var dot = operation.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && files.TryGetValue(operation[..dot], out var kind))
        {
            return kind;
        }

        return files.GetValueOrDefault(Default);
    }

    /// <summary>The script for one label.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values, string btwPath, string? printer, int copies, string jobName)
    {
        var builtIn = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BtwPath"] = Escape(btwPath),
            ["Printer"] = Escape(printer ?? ""),
            ["Copies"] = copies.ToString(CultureInfo.InvariantCulture),
            ["JobName"] = Escape(jobName),
            ["NamedSubStrings"] = NamedSubStrings(values),
            ["PrintSetup"] = PrintSetup(printer, copies),
        };
        return Placeholder().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            return builtIn.TryGetValue(name, out var block) ? block : Escape(values.GetValueOrDefault(name) ?? "");
        });
    }

    /// <summary>What is wrong with a template, filled in with sample values; empty when it is a BTXML print script.</summary>
    public static IReadOnlyList<string> Problems(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return ["Give the BTXML template; start from the standard one."];
        }

        if (template.Length > MaxTemplate)
        {
            return [$"Keep the template to {MaxTemplate} characters."];
        }

        var sample = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Code"] = "PUL1L1", ["Title"] = "Sample <&> \"label\"", ["Line1"] = "Line" };
        XDocument script;
        try
        {
            script = XDocument.Parse(Render(template, sample, @"C:\Labels\Sample.btw", "Sample printer", 1, "Pulaski-check"));
        }
        catch (XmlException ex)
        {
            return [$"The template isn't well-formed XML once filled in: {ex.Message}"];
        }

        var problems = new List<string>();
        if (script.Root?.Name.LocalName != "XMLScript")
        {
            problems.Add("A BTXML script's root element is XMLScript.");
        }

        if (!script.Descendants().Any(e => e.Name.LocalName == "Print" && e.Elements().Any(f => f.Name.LocalName == "Format")))
        {
            problems.Add("The script needs a Print command with a Format (use {{BtwPath}} for the .btw document).");
        }

        return problems;
    }

    /// <summary>What is wrong with a document path; null when it can be used.</summary>
    public static string? PathProblem(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "Give the .btw document's path."
        : path.Length > MaxPath ? $"Keep a path to {MaxPath} characters."
        : path.Any(char.IsControl) ? "A path can't hold control characters."
        : !path.Trim().EndsWith(".btw", StringComparison.OrdinalIgnoreCase) ? "A BarTender document ends in .btw."
        : null;

    /// <summary>Whether <paramref name="operation"/> can name a document: <c>default</c>, a kind, or a kind and what printed it (<c>lot.produced</c>).</summary>
    public static bool IsOperation(string operation) => OperationName().IsMatch(operation);

    /// <summary>
    /// The error BarTender's answer reports, if any (#168): the text of a <c>Message</c> of severity Error; null when the answer isn't BTXML
    /// or reports none.
    /// </summary>
    public static string? ErrorIn(string? response)
    {
        if (string.IsNullOrWhiteSpace(response) || response.TrimStart()[0] != '<')
        {
            return null;
        }

        try
        {
            var errors = XDocument.Parse(response).Descendants()
                .Where(e => e.Name.LocalName == "Message"
                    && string.Equals((string?)e.Attribute("Severity"), "Error", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Elements().FirstOrDefault(t => t.Name.LocalName == "Text")?.Value ?? e.Value).Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return errors.Count == 0 ? null : string.Join(" ", errors);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string NamedSubStrings(IReadOnlyDictionary<string, string?> values)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in values.Where(v => !NotSent.Contains(v.Key)).OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            block.Append(CultureInfo.InvariantCulture, $"<NamedSubString Name=\"{Escape(name)}\"><Value>{Escape(value ?? "")}</Value></NamedSubString>");
        }

        return block.ToString();
    }

    private static string PrintSetup(string? printer, int copies) =>
        "<PrintSetup>" + (printer is { Length: > 0 } ? $"<Printer>{Escape(printer)}</Printer>" : "")
        + $"<IdenticalCopiesOfLabel>{copies.ToString(CultureInfo.InvariantCulture)}</IdenticalCopiesOfLabel></PrintSetup>";

    // XML-escaped, without the control characters XML 1.0 can't hold.
    private static string Escape(string value) =>
        SecurityElement.Escape(new string([.. value.Where(c => !char.IsControl(c) || c is '\t' or '\n' or '\r')])) ?? "";

    [GeneratedRegex(@"\{\{([A-Za-z0-9]+)\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,29}(\\.[a-z][a-z0-9_]{0,29})?$")]
    private static partial Regex OperationName();
}
