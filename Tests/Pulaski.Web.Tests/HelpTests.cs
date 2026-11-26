using System.Text.RegularExpressions;
using Pulaski.Web.Services.Help;

namespace Pulaski.Web.Tests;

/// <summary>Every screen's help opens the right section of the user guide, and the guide's own links land (#130).</summary>
public partial class HelpTests
{
    private static readonly Lazy<UserGuide> Guide = new(() => new UserGuide(File.ReadAllText(Path.Combine(Root(), "design", "user_guide.md"))));

    [Fact]
    public void Every_page_has_a_help_section()
    {
        var routes = Directory.EnumerateFiles(Path.Combine(Root(), "src", "Pulaski.Web", "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => PageRoute().Matches(File.ReadAllText(f)).Select(m => HelpTopics.Normalize(m.Groups[1].Value)))
            .Distinct()
            .ToList();
        Assert.NotEmpty(routes);
        var mapped = HelpTopics.All.Select(t => t.Route).ToHashSet(StringComparer.Ordinal);
        var unmapped = routes.Where(r => !mapped.Contains(r) && !HelpTopics.None.Contains(r)).ToList();
        var stale = mapped.Where(r => !routes.Contains(r)).ToList();
        Assert.True(unmapped.Count == 0 && stale.Count == 0, $"Without help: {string.Join(", ", unmapped)}; not a page: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Every_help_section_is_in_the_guide()
    {
        var anchors = Guide.Value.Anchors.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(HelpTopics.All.Where(t => !anchors.Contains(t.Anchor)).Select(t => $"{t.Route} → {t.Anchor}"));
    }

    [Fact]
    public void Every_link_inside_the_guide_lands_on_a_heading()
    {
        var anchors = Guide.Value.Anchors.ToHashSet(StringComparer.Ordinal);
        var broken = GuideLink().Matches(Guide.Value.Html).Select(m => m.Groups[1].Value).Distinct().Where(a => !anchors.Contains(a)).ToList();
        Assert.True(broken.Count == 0, "Broken links in the guide: " + string.Join(", ", broken));
    }

    [Fact]
    public void A_path_finds_its_section_ignoring_ids_query_and_fragment()
    {
        Assert.Equal("the-overview-page", HelpTopics.For(""));
        Assert.Equal("the-job-page", HelpTopics.For($"jobs/{Guid.NewGuid()}?tab=runs#reports"));
        Assert.Equal("set-control-limits", HelpTopics.For($"quality/characteristics/{Guid.NewGuid()}/control-limits"));
        Assert.Equal("find-a-lot-the-lots-tab", HelpTopics.For("Inventory/Lots"));
        Assert.Null(HelpTopics.For("nowhere/at/all"));
        Assert.Equal("jobs/{}", HelpTopics.Normalize("/jobs/{JobId:guid}"));

        var section = Guide.Value.Section("the-job-page")!;
        Assert.Equal(("The job page", 3), (section.Title, section.Level));
        Assert.StartsWith("<h3 id=\"the-job-page\">", section.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3 id=\"reverse-a-production-report\"", section.Html, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Pulaski.Web")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }

    [GeneratedRegex(@"^@page ""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex PageRoute();

    [GeneratedRegex("""href="help#([^"]+)""")]
    private static partial Regex GuideLink();
}
