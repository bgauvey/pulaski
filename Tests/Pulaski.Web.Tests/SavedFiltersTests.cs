using System.Text.RegularExpressions;

namespace Pulaski.Web.Tests;

/// <summary>Every list page keeps its filters under a key the server accepts (#131), and no two pages share one.</summary>
public partial class SavedFiltersTests
{
    [Fact]
    public void Every_pages_filter_key_is_one_the_server_keeps_and_is_its_own()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "src", "Pulaski.Web")))
        {
            root = root.Parent!;
        }

        var pages = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Pulaski.Web", "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Key().Matches(File.ReadAllText(f)).Select(m => m.Groups["page"].Value))
            .ToList();
        Assert.True(pages.Count >= 13, $"Only {pages.Count} pages keep filters.");
        Assert.Equal(pages.Count, pages.Distinct(StringComparer.Ordinal).Count());
        Assert.All(pages, p => Assert.Matches(ServerKey(), $"filters:{p}"));
    }

    // The pages' keys: `const string Page = "…"` with SavedFilters, or `UserViews.FiltersKey("…")`.
    [GeneratedRegex("""(?:const string Page = |UserViews\.FiltersKey\()"(?<page>[^"]+)"[;)]""")]
    private static partial Regex Key();

    // As sec.user_setting's CHECK and UserSettingKeys.
    [GeneratedRegex("^(home|favorites|filters:[a-z0-9/_-]{1,80})$")]
    private static partial Regex ServerKey();
}
