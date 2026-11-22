using System.Text.RegularExpressions;

namespace Pulaski.Web.Services.Localization;

/// <summary>
/// Finds the terms the web's source translates (#128): every literal <c>L["…"]</c> (<c>l["…"]</c> in helpers given the text) and
/// <c>Term.Mark("…")</c>. The term catalog
/// (<c>Pulaski.Contracts/Localization/terms.txt</c>) is these, sorted; a test keeps the two equal. Pure.
/// </summary>
public static partial class TermExtractor
{
    public static IEnumerable<string> Terms(string source) =>
        Pattern().Matches(source).Select(m => Unescape(m.Groups["indexer"].Success ? m.Groups["indexer"].Value : m.Groups["mark"].Value));

    /// <summary>
    /// The templates of the server's messages for people (#128): every <c>new DomainRuleException("…")</c> (and the not found, conflict and
    /// not allowed ones) whose one argument is a string, read as <c>MessageBuilder</c> builds them: each hole a numbered placeholder with its
    /// alignment and format. A message put together otherwise isn't found, and shows in English.
    /// </summary>
    public static IEnumerable<string> Messages(string source)
    {
        foreach (Match match in ThrowPattern().Matches(source))
        {
            // Literals joined with '+' are one message when all are interpolated, or all plain: C# builds them as one.
            var at = match.Index + match.Length;
            var holes = 0;
            var template = new System.Text.StringBuilder();
            bool? interpolated = null;
            while (ReadMessage(source, ref at, ref holes, out var isInterpolated) is { } part && (interpolated ?? isInterpolated) == isInterpolated)
            {
                (interpolated, _) = (isInterpolated, template.Append(part));
                if (Next(source, at) == ')')
                {
                    yield return template.ToString();
                    break;
                }

                if (Next(source, at) != '+')
                {
                    break;
                }

                at = source.IndexOf('+', at) + 1;
            }
        }
    }

    /// <summary>The catalog's text for these terms: distinct, in ordinal order, one per line.</summary>
    public static string Catalog(IEnumerable<string> terms) =>
        string.Join("\n", terms.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "\n";

    private static char Next(string source, int at)
    {
        while (at < source.Length && char.IsWhiteSpace(source[at]))
        {
            at++;
        }

        return at < source.Length ? source[at] : '\0';
    }

    /// <summary>Reads a plain or interpolated string literal at <paramref name="at"/> into its template; null when there isn't one.</summary>
    private static string? ReadMessage(string source, ref int at, ref int holes, out bool interpolated)
    {
        while (at < source.Length && char.IsWhiteSpace(source[at]))
        {
            at++;
        }

        interpolated = at < source.Length && source[at] == '$';
        if (interpolated)
        {
            at++;
        }

        if (at >= source.Length || source[at] != '"')
        {
            return null;
        }

        at++;
        var template = new System.Text.StringBuilder();
        while (at < source.Length)
        {
            var c = source[at];
            if (c == '"')
            {
                at++;
                return template.ToString();
            }

            if (c == '\\' && at + 1 < source.Length)
            {
                template.Append(source[at + 1] switch { 'n' => '\n', 't' => '\t', var other => other });
                at += 2;
            }
            else if (!interpolated && (c == '{' || c == '}'))
            {
                template.Append(c).Append(c);
                at++;
            }
            else if (interpolated && (c == '{' || c == '}') && at + 1 < source.Length && source[at + 1] == c)
            {
                template.Append(c).Append(c);
                at += 2;
            }
            else if (interpolated && c == '{')
            {
                template.Append('{').Append(holes++.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(ReadHole(source, ref at)).Append('}');
            }
            else
            {
                template.Append(c);
                at++;
            }
        }

        return null;
    }

    /// <summary>
    /// Skips a hole's expression from its <c>{</c> to past its <c>}</c>, returning its alignment and format (<c>,5:N0</c>): the first ',' or
    /// ':' outside brackets and quotes (C# wants a conditional in a hole in parentheses).
    /// </summary>
    private static string ReadHole(string source, ref int at)
    {
        at++;
        var depth = 0;
        var tail = -1;
        while (at < source.Length)
        {
            var c = source[at];
            if (c == '"')
            {
                at++;
                while (at < source.Length && source[at] != '"')
                {
                    at += source[at] == '\\' ? 2 : 1;
                }
            }
            else if (c == '\'')
            {
                at++;
                while (at < source.Length && source[at] != '\'')
                {
                    at += source[at] == '\\' ? 2 : 1;
                }
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' || (c == '}' && depth > 0))
            {
                depth--;
            }
            else if (c == '}')
            {
                var spec = tail < 0 ? "" : source[tail..at].Trim();
                at++;
                return spec;
            }
            else if (depth == 0 && tail < 0 && c is ',' or ':')
            {
                tail = at;
            }

            at++;
        }

        return "";
    }

    private static string Unescape(string literal) => literal.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"\bnew\s+(?:DomainRuleException|NotFoundException|ConflictException|ForbiddenException)\s*\(")]
    private static partial Regex ThrowPattern();

    [GeneratedRegex(@"\b[Ll]\[""(?<indexer>(?:[^""\\]|\\.)+)""|\bTerm\.Mark\(""(?<mark>(?:[^""\\]|\\.)+)""\)")]
    private static partial Regex Pattern();
}
