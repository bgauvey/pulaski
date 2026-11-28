using Pulaski.Web.Tests.Accessibility;

namespace Pulaski.Web.Tests;

/// <summary>
/// Blazor refuses to re-render siblings that share a <c>@key</c> ("More than one sibling ... has the same key value"), which took the lot
/// page down for anyone seeing quality. Components written once in a file with the same key expression are siblings, or may become so
/// when their <c>@if</c>s are both true; each needs a key of its own.
/// </summary>
public class ComponentKeyTests
{
    [Fact]
    public void Components_in_one_file_never_share_a_key()
    {
        var root = Root();
        var findings = Directory.EnumerateFiles(Path.Combine(root, "src", "Pulaski.Web", "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => RazorMarkup.Tags(Path.GetRelativePath(root, f), File.ReadAllText(f))
                .Where(t => char.IsUpper(t.Name[0]) && t["@key"] is not null)
                .GroupBy(t => t["@key"])
                .Where(g => g.Count() > 1)
                .Select(g => $"{string.Join(", ", g)} share @key=\"{g.Key}\""))
            .ToList();
        Assert.True(findings.Count == 0, string.Join("\n", findings));
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
}
