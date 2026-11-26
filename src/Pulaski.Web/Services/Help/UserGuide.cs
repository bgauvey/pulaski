using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Pulaski.Web.Services.Help;

/// <summary>A section of the user guide: its heading's anchor and level, its title, and its HTML down to the next heading at its level.</summary>
public sealed record GuideSection(string Id, int Level, string Title, string Html);

/// <summary>
/// The user guide (#130, D-130), rendered once from the Markdown shipped with the app, with GitHub-style heading anchors so the guide's own
/// links work. Raw HTML in the Markdown is escaped. Links to another section point at the help page, which serves the whole guide.
/// </summary>
public sealed partial class UserGuide
{
    private readonly Dictionary<string, GuideSection> _sections;

    public UserGuide(string markdown)
    {
        // Headings get GitHub's anchors (as the guide's links are written), not Markdig's own, which drop a leading number.
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var document = Markdown.Parse(markdown, pipeline);
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            var slug = Slug(PlainText(heading.Inline));
            var id = used.TryGetValue(slug, out var n) ? $"{slug}-{n}" : slug;
            used[slug] = n + 1;
            heading.GetAttributes().Id = id;
        }

        Html = AnchorLink().Replace(document.ToHtml(pipeline), "href=\"help#${id}\"");
        var headings = Heading().Matches(Html);
        _sections = new(StringComparer.Ordinal);
        for (var i = 0; i < headings.Count; i++)
        {
            var level = int.Parse(headings[i].Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var end = headings.Skip(i + 1).FirstOrDefault(h => int.Parse(h.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture) <= level)?.Index ?? Html.Length;
            var id = headings[i].Groups["id"].Value;
            var title = Tags().Replace(headings[i].Groups["title"].Value, "");
            _sections[id] = new GuideSection(id, level, System.Net.WebUtility.HtmlDecode(title), Html[headings[i].Index..end]);
        }
    }

    /// <summary>A heading's anchor as GitHub makes it: lower case, letters, digits, hyphens and underscores kept, spaces as hyphens.</summary>
    public static string Slug(string title) =>
        new string([.. title.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')]).Replace(' ', '-');

    private static string PlainText(ContainerInline? inline)
    {
        var text = new System.Text.StringBuilder();
        foreach (var node in inline?.Descendants() ?? [])
        {
            switch (node)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>The whole guide as HTML.</summary>
    public string Html { get; }

    public IReadOnlyCollection<string> Anchors => _sections.Keys;

    public GuideSection? Section(string? id) => id is not null && _sections.TryGetValue(id, out var s) ? s : null;

    /// <summary>Reads the guide shipped beside the app (<c>Help/user_guide.md</c>).</summary>
    public static UserGuide Load(string baseDirectory) =>
        new(File.ReadAllText(Path.Combine(baseDirectory, "Help", "user_guide.md")));

    [GeneratedRegex("""href="#(?<id>[^"]+)""")]
    private static partial Regex AnchorLink();

    [GeneratedRegex("""<h(?<level>[2-4]) id="(?<id>[^"]+)">(?<title>.*?)</h\k<level>>""", RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
