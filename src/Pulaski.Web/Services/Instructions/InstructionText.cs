using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Pulaski.Web.Services.Instructions;

/// <summary>
/// An instruction's text as HTML (#126a): lines starting <c>#</c> or <c>##</c> are headings, <c>1.</c> numbered steps, <c>-</c> or <c>*</c>
/// points, <c>**words**</c> bold, a blank line a new paragraph. Everything else is escaped, so the text can hold no markup of its own. Pure.
/// </summary>
public static partial class InstructionText
{
    public static string ToHtml(string? text)
    {
        var html = new StringBuilder();
        string? list = null;
        var paragraph = new List<string>();

        void EndParagraph()
        {
            if (paragraph.Count > 0)
            {
                html.Append("<p>").Append(string.Join("<br>", paragraph)).Append("</p>");
                paragraph.Clear();
            }
        }

        void EndList()
        {
            if (list is not null)
            {
                html.Append("</").Append(list).Append('>');
                list = null;
            }
        }

        void Item(string kind, string content)
        {
            EndParagraph();
            if (list != kind)
            {
                EndList();
                html.Append('<').Append(kind).Append('>');
                list = kind;
            }

            html.Append("<li>").Append(Inline(content)).Append("</li>");
        }

        foreach (var raw in (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                EndParagraph();
                EndList();
            }
            else if (Heading().Match(line) is { Success: true } h)
            {
                EndParagraph();
                EndList();
                var level = h.Groups[1].Value.Length == 1 ? "h3" : "h4";
                html.Append('<').Append(level).Append('>').Append(Inline(h.Groups[2].Value)).Append("</").Append(level).Append('>');
            }
            else if (Numbered().Match(line) is { Success: true } n)
            {
                Item("ol", n.Groups[1].Value);
            }
            else if (Point().Match(line) is { Success: true } b)
            {
                Item("ul", b.Groups[1].Value);
            }
            else
            {
                EndList();
                paragraph.Add(Inline(line.Trim()));
            }
        }

        EndParagraph();
        EndList();
        return html.ToString();
    }

    private static string Inline(string text) => Bold().Replace(WebUtility.HtmlEncode(text.Trim()), "<strong>$1</strong>");

    [GeneratedRegex(@"^\s*(#{1,2})\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*\d+[.)]\s+(.+)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^\s*[-*]\s+(.+)$")]
    private static partial Regex Point();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
