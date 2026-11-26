namespace Pulaski.Web.Tests.Accessibility;

/// <summary>
/// WCAG 2.2 checks that can be read from the markup (#142, D-127), over every .razor file of the web app: every form control has a label
/// (1.3.1, 3.3.2, 4.1.2), every button and link a name (2.4.4, 4.1.2), every image a text alternative (1.1.1).
/// </summary>
public class MarkupAccessibilityTests
{
    private static readonly HashSet<string> Controls = ["input", "select", "textarea", "InputText", "InputNumber", "InputSelect", "InputDate", "InputCheckbox", "InputTextArea"];
    private static readonly HashSet<string> Unlabelled = ["hidden", "submit", "button", "reset", "image"];

    [Fact]
    public void Every_form_control_has_a_label() => AssertNone(Findings(UnlabelledControls));

    [Fact]
    public void Every_button_and_link_has_a_name() => AssertNone(Findings(UnnamedActions));

    [Fact]
    public void Every_image_has_a_text_alternative() => AssertNone(Findings((_, tags) => tags.Where(t => t.Name == "img" && !t.Has("alt"))));

    // 1.3.1: a label names a control that is there; one pointing at nothing labels nothing.
    [Fact]
    public void Every_label_points_at_a_control() => AssertNone(Findings((text, tags) =>
    {
        // An id may be written as markup (`x-@Id`) on one side and as C# (`@($"x-{Id}")`, or a helper's "x" argument) on the other.
        var ids = tags.Select(t => t["id"] ?? t["Id"]).OfType<string>().Select(Normalized).ToHashSet(StringComparer.Ordinal);
        return tags.Where(t => t.Name == "label" && t["for"] is { } target
            && !ids.Contains(Normalized(target)) && !text.Contains($"\"{target}\"", StringComparison.Ordinal));
    }));

    private static string Normalized(string id) => new([.. id.Where(c => c is not ('@' or '$' or '"' or '{' or '}' or '(' or ')'))]);

    // 2.4.2: every routable page has a title.
    [Fact]
    public void Every_page_has_a_title() => AssertNone(Findings((text, tags) =>
        text.TrimStart().StartsWith("@page", StringComparison.Ordinal) && !tags.Any(t => t.Name == "PageTitle") ? tags.Take(1) : []));

    // 2.4.3: no positive tabindex, which would take focus out of reading order.
    [Fact]
    public void Focus_follows_reading_order() => AssertNone(Findings((_, tags) =>
        tags.Where(t => t["tabindex"] is { } v && int.TryParse(v, out var n) && n > 0)));

    [Fact]
    public void The_page_says_its_language_skips_to_its_content_and_hides_icons_from_screen_readers()
    {
        var root = Root();
        Assert.Contains("<html lang=\"@L.Language\">", File.ReadAllText(Path.Combine(root, "src", "Pulaski.Web", "Components", "App.razor")), StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\" focusable=\"false\"", File.ReadAllText(Path.Combine(root, "src", "Pulaski.Web", "Components", "Shared", "Icon.razor")), StringComparison.Ordinal);

        // 2.4.1: a skip link first, to the main region, which can take focus.
        var layout = File.ReadAllText(Path.Combine(root, "src", "Pulaski.Web", "Components", "Layout", "MainLayout.razor"));
        Assert.True(layout.IndexOf("class=\"skip-link", StringComparison.Ordinal) is var skip and >= 0 && skip < layout.IndexOf("<header", StringComparison.Ordinal));
        Assert.Contains("id=\"main-content\" tabindex=\"-1\"", layout, StringComparison.Ordinal);
    }

    internal static IEnumerable<MarkupTag> UnlabelledControls(string text, IReadOnlyList<MarkupTag> tags)
    {
        var labels = tags.Where(t => t.Name == "label").ToList();
        var forIds = labels.Select(l => l["for"]).OfType<string>().Select(Normalized).ToHashSet(StringComparer.Ordinal);
        return tags.Where(t => Controls.Contains(t.Name)
            && !(t["type"] is { } type && Unlabelled.Contains(type))
            && !t.Has("@attributes")
            && !Named(t)
            && !(t["id"] is { } id && forIds.Contains(Normalized(id)))
            && !labels.Any(l => l.Start < t.Start && t.Start < l.End + 1 + (l.Inner?.Length ?? 0)));
    }

    internal static IEnumerable<MarkupTag> UnnamedActions(string text, IReadOnlyList<MarkupTag> tags) =>
        tags.Where(t => t.Name is "button" or "a" or "NavLink" or "ScreenLink" or "ActionButton"
            && !t.Has("@attributes") && !Named(t) && RazorMarkup.VisibleText(t.Inner).Length == 0
            && !(t.Name == "a" && !t.Has("href")));

    private static bool Named(MarkupTag t) =>
        t.Has("aria-label") || t.Has("aria-labelledby") || t.Has("title") || t.Has("AriaLabel") || t.Has("Title");

    private static List<string> Findings(Func<string, IReadOnlyList<MarkupTag>, IEnumerable<MarkupTag>> rule) =>
        [.. RazorFiles().SelectMany(f =>
        {
            var text = File.ReadAllText(f);
            var relative = Path.GetRelativePath(Root(), f);
            return rule(text, RazorMarkup.Tags(relative, text)).Select(t => t.ToString());
        })];

    private static void AssertNone(List<string> findings) =>
        Assert.True(findings.Count == 0, $"{findings.Count} found:\n" + string.Join("\n", findings));

    private static IEnumerable<string> RazorFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", "Pulaski.Web", "Components"), "*.razor", SearchOption.AllDirectories);

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
