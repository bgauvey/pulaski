using System.Text;

namespace Pulaski.Web.Tests.Accessibility;

/// <summary>A tag in a .razor file: its name, raw attributes, where it starts, and (for elements that need a name) its inner markup.</summary>
public sealed record MarkupTag(string File, int Line, string Name, IReadOnlyDictionary<string, string?> Attributes, int Start, int End, string? Inner)
{
    public bool Has(string attribute) => Attributes.ContainsKey(attribute);

    public string? this[string attribute] => Attributes.GetValueOrDefault(attribute);

    public override string ToString() => $"{File}:{Line} <{Name}>";
}

/// <summary>
/// Reads tags out of Razor markup well enough for accessibility checks (#142): attribute values may hold Razor expressions with quotes and
/// parentheses of their own, which don't end the value. Code blocks (<c>@code { }</c>) are left out. Not a full Razor parser.
/// </summary>
public static class RazorMarkup
{
    public static IReadOnlyList<MarkupTag> Tags(string file, string text)
    {
        var markup = WithoutCode(text);
        var tags = new List<MarkupTag>();
        for (var i = 0; i < markup.Length; i++)
        {
            if (markup[i] != '<' || i + 1 >= markup.Length || !char.IsLetter(markup[i + 1]))
            {
                continue;
            }

            var nameEnd = i + 1;
            while (nameEnd < markup.Length && (char.IsLetterOrDigit(markup[nameEnd]) || markup[nameEnd] is '.' or '-' or ':'))
            {
                nameEnd++;
            }

            var name = markup[(i + 1)..nameEnd];
            var (attributes, end) = ReadAttributes(markup, nameEnd);
            if (end < 0)
            {
                continue;
            }

            var selfClosing = markup[end - 1] == '/';
            string? inner = null;
            if (!selfClosing && name is "button" or "a" or "NavLink" or "label" or "ActionButton" or "ScreenLink" or "summary" or "th" or "h1" or "h2" or "h3")
            {
                inner = InnerOf(markup, name, end + 1);
            }

            tags.Add(new MarkupTag(file, LineOf(markup, i), name, attributes, i, end, inner));
            i = end;
        }

        return tags;
    }

    /// <summary>The text a reader would hear from inner markup: tags removed, Razor expressions kept as "text".</summary>
    public static string VisibleText(string? inner)
    {
        if (inner is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        var depth = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '<' && i + 1 < inner.Length && (char.IsLetter(inner[i + 1]) || inner[i + 1] == '/'))
            {
                depth++;
                continue;
            }

            if (c == '>' && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth == 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    // Removes @code { … } (and @functions) blocks, keeping line numbers.
    private static string WithoutCode(string text)
    {
        var sb = new StringBuilder(text);
        foreach (var keyword in new[] { "@code", "@functions" })
        {
            var at = text.IndexOf(keyword + " {", StringComparison.Ordinal);
            if (at < 0)
            {
                at = text.IndexOf(keyword + "\n{", StringComparison.Ordinal);
            }

            if (at >= 0)
            {
                for (var i = at; i < text.Length; i++)
                {
                    if (sb[i] != '\n')
                    {
                        sb[i] = ' ';
                    }
                }
            }
        }

        // Razor comments.
        var s = sb.ToString();
        var start = 0;
        while ((start = s.IndexOf("@*", start, StringComparison.Ordinal)) >= 0)
        {
            var stop = s.IndexOf("*@", start + 2, StringComparison.Ordinal);
            stop = stop < 0 ? s.Length : stop + 2;
            for (var i = start; i < stop; i++)
            {
                if (sb[i] != '\n')
                {
                    sb[i] = ' ';
                }
            }

            start = stop;
        }

        return sb.ToString();
    }

    private static (Dictionary<string, string?> Attributes, int End) ReadAttributes(string s, int i)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= s.Length)
            {
                return (attributes, -1);
            }

            if (s[i] == '>')
            {
                return (attributes, i);
            }

            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '>')
            {
                return (attributes, i + 1);
            }

            var nameStart = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('=' or '>') && !(s[i] == '/' && i + 1 < s.Length && s[i + 1] == '>'))
            {
                if (s[i] == '(')
                {
                    i = SkipParens(s, i);
                    continue;
                }

                i++;
            }

            var name = s[nameStart..i];
            if (i < s.Length && s[i] == '=')
            {
                i++;
                var (value, next) = ReadValue(s, i);
                attributes[name] = value;
                i = next;
            }
            else if (name.Length > 0)
            {
                attributes[name] = null;
            }
            else
            {
                i++;
            }
        }

        return (attributes, -1);
    }

    private static (string Value, int Next) ReadValue(string s, int i)
    {
        if (i < s.Length && s[i] is '"' or '\'')
        {
            var quote = s[i];
            var start = ++i;
            while (i < s.Length && s[i] != quote)
            {
                if (s[i] == '(' && i > 0 && s[i - 1] == '@')
                {
                    i = SkipParens(s, i);
                    continue;
                }

                i++;
            }

            return (s[start..Math.Min(i, s.Length)], i + 1);
        }

        var bare = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '>')
        {
            if (s[i] == '(')
            {
                i = SkipParens(s, i);
                continue;
            }

            i++;
        }

        return (s[bare..i], i);
    }

    // From an opening parenthesis to just past its match, skipping C# string literals.
    private static int SkipParens(string s, int i)
    {
        var depth = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    i += s[i] == '\\' ? 2 : 1;
                }

                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i + 1;
            }
        }

        return s.Length;
    }

    private static string InnerOf(string s, string name, int from)
    {
        var depth = 1;
        var i = from;
        while (i < s.Length)
        {
            var open = s.IndexOf("<" + name, i, StringComparison.Ordinal);
            var close = s.IndexOf("</" + name + ">", i, StringComparison.Ordinal);
            if (close < 0)
            {
                return s[from..];
            }

            if (open >= 0 && open < close && open + name.Length + 1 < s.Length && !char.IsLetterOrDigit(s[open + name.Length + 1]))
            {
                depth++;
                i = open + name.Length + 1;
                continue;
            }

            if (--depth == 0)
            {
                return s[from..close];
            }

            i = close + name.Length + 3;
        }

        return s[from..];
    }

    private static int LineOf(string s, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (s[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
