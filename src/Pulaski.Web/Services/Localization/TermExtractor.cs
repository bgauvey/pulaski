using System.Text.RegularExpressions;

namespace Pulaski.Web.Services.Localization;

/// <summary>
/// Finds the terms the web's source translates (#128): every literal <c>L["…"]</c> and <c>Term.Mark("…")</c>. The term catalog
/// (<c>Pulaski.Contracts/Localization/terms.txt</c>) is these, sorted; a test keeps the two equal. Pure.
/// </summary>
public static partial class TermExtractor
{
    public static IEnumerable<string> Terms(string source) =>
        Pattern().Matches(source).Select(m => Unescape(m.Groups["indexer"].Success ? m.Groups["indexer"].Value : m.Groups["mark"].Value));

    /// <summary>The catalog's text for these terms: distinct, in ordinal order, one per line.</summary>
    public static string Catalog(IEnumerable<string> terms) =>
        string.Join("\n", terms.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "\n";

    private static string Unescape(string literal) => literal.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"\bL\[""(?<indexer>(?:[^""\\]|\\.)+)""|\bTerm\.Mark\(""(?<mark>(?:[^""\\]|\\.)+)""\)")]
    private static partial Regex Pattern();
}
