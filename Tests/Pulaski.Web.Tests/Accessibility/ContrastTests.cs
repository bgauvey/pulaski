using System.Globalization;
using System.Text.RegularExpressions;

namespace Pulaski.Web.Tests.Accessibility;

/// <summary>
/// Colour contrast (#142, D-127), worked out from the stylesheets in both themes: text 4.5:1 (WCAG 1.4.3), controls' borders, state colours and
/// the focus ring 3:1 against what they sit on (1.4.11). Every CSS rule that sets both a text colour and a background is checked too.
/// </summary>
public partial class ContrastTests
{
    private static readonly (string Fore, string[] Backs, double Need)[] TokenPairs =
    [
        ("text", ["bg", "surface", "surface-2"], 4.5),
        ("text-muted", ["bg", "surface", "surface-2"], 4.5),
        ("text-faint", ["bg", "surface", "surface-2"], 4.5),
        ("accent", ["bg", "surface", "surface-2", "accent-soft"], 4.5),
        ("accent-ink", ["accent"], 4.5),
        ("success", ["bg", "surface"], 4.5),
        ("warning", ["bg", "surface"], 4.5),
        ("danger", ["bg", "surface"], 4.5),
        ("border-strong", ["bg", "surface"], 3),
        ("focus-ring", ["bg", "surface"], 3),
        ("state-producing", ["surface"], 3),
        ("state-waiting", ["surface"], 3),
        ("state-held", ["surface"], 3),
        ("state-stopped", ["surface"], 3),
        ("state-ready", ["surface"], 3),
        ("state-nosignal", ["surface"], 3),
    ];

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_design_tokens_keep_their_contrast(string theme)
    {
        var tokens = Themes()[theme];
        var failures = TokenPairs
            .SelectMany(p => p.Backs.Select(b => (p.Fore, Back: b, p.Need, Ratio: Ratio(Resolve($"var(--{p.Fore})", tokens)!.Value, Resolve($"var(--{b})", tokens)!.Value))))
            .Where(x => x.Ratio < x.Need)
            .Select(x => $"{x.Fore} on {x.Back}: {x.Ratio:0.00} (needs {x.Need})")
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Every_rule_with_a_colour_and_a_background_keeps_text_readable(string theme)
    {
        var tokens = Themes()[theme];
        var failures = new List<string>();
        foreach (var file in StyleSheets().Where(f => Path.GetFileName(f) != "tokens.css"))
        {
            foreach (var (selector, declarations) in Rules(File.ReadAllText(file)))
            {
                if (theme == "light" && selector.Contains("dark", StringComparison.Ordinal)
                    || !declarations.TryGetValue("color", out var color)
                    || !(declarations.TryGetValue("background-color", out var background) || declarations.TryGetValue("background", out background))
                    || Resolve(color, tokens) is not { } fore || Resolve(background, tokens) is not { } back)
                {
                    continue;
                }

                if (Ratio(fore, back) is var ratio and < 4.5)
                {
                    failures.Add($"{Path.GetFileName(file)} {selector}: {color} on {background} = {ratio:0.00}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void The_ratio_follows_the_wcag_formula()
    {
        Assert.Equal(21, Ratio((0, 0, 0), (255, 255, 255)), 2);
        Assert.Equal(1, Ratio((119, 119, 119), (119, 119, 119)), 2);
        Assert.Equal(4.48, Ratio((119, 119, 119), (255, 255, 255)), 2);
    }

    // Each theme's custom properties: the tokens, then every other stylesheet's :root properties (such as --sched-danger-soft), unresolved.
    private static Dictionary<string, Dictionary<string, string>> Themes()
    {
        var tokens = File.ReadAllText(Path.Combine(CssDir(), "tokens.css"));
        Dictionary<string, string> Block(string selector)
        {
            var start = tokens.IndexOf(selector, StringComparison.Ordinal);
            var body = tokens[start..tokens.IndexOf('}', start)];
            return Property().Matches(body).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        }

        var light = Block(":root {");
        var dark = new Dictionary<string, string>(light);
        foreach (var (k, v) in Block(":root[data-theme=\"dark\"] {"))
        {
            dark[k] = v;
        }

        foreach (var file in StyleSheets())
        {
            foreach (var (selector, declarations) in Rules(File.ReadAllText(file)).Where(r => r.Selector == ":root"))
            {
                foreach (var (k, v) in declarations.Where(d => d.Key.StartsWith("--", StringComparison.Ordinal)))
                {
                    light.TryAdd(k[2..], v);
                    dark.TryAdd(k[2..], v);
                }
            }
        }

        return new() { ["light"] = light, ["dark"] = dark };
    }

    private static (double R, double G, double B)? Resolve(string value, Dictionary<string, string> tokens, int depth = 0)
    {
        var v = value.Replace("!important", "", StringComparison.Ordinal).Trim();
        if (depth > 10)
        {
            return null;
        }

        if (VarPattern().Match(v) is { Success: true } var && var.Length == v.Length)
        {
            return tokens.TryGetValue(var.Groups[1].Value, out var t) ? Resolve(t, tokens, depth + 1)
                : var.Groups[2].Success ? Resolve(var.Groups[2].Value, tokens, depth + 1) : null;
        }

        if (MixPattern().Match(v) is { Success: true } mix && mix.Length == v.Length
            && Resolve(mix.Groups[1].Value, tokens, depth + 1) is { } a && Resolve(mix.Groups[3].Value, tokens, depth + 1) is { } b)
        {
            var p = double.Parse(mix.Groups[2].Value, CultureInfo.InvariantCulture) / 100;
            return (a.R * p + b.R * (1 - p), a.G * p + b.G * (1 - p), a.B * p + b.B * (1 - p));
        }

        v = v switch { "white" or "#fff" => "#ffffff", "black" or "#000" => "#000000", _ => v };
        return HexPattern().IsMatch(v)
            ? (Convert.ToInt32(v[1..3], 16), Convert.ToInt32(v[3..5], 16), Convert.ToInt32(v[5..7], 16))
            : null;
    }

    private static double Ratio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        static double Channel(double c) => c / 255 <= 0.03928 ? c / 255 / 12.92 : Math.Pow((c / 255 + 0.055) / 1.055, 2.4);
        static double Luminance((double R, double G, double B) c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Innermost rules (no nesting), with comments removed; at-rules' own braces are skipped by matching only blocks without braces inside.
    private static IEnumerable<(string Selector, Dictionary<string, string> Declarations)> Rules(string css) =>
        RulePattern().Matches(CommentPattern().Replace(css, "")).Select(m => (
            m.Groups[1].Value.Trim(),
            DeclarationPattern().Matches(m.Groups[2].Value).GroupBy(d => d.Groups[1].Value.Trim()).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim())));

    private static IEnumerable<string> StyleSheets() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", "Pulaski.Web"), "*.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string CssDir() => Path.Combine(Root(), "src", "Pulaski.Web", "wwwroot", "css");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Pulaski.Web")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }

    [GeneratedRegex(@"--([\w-]+):\s*([^;]+);")]
    private static partial Regex Property();

    [GeneratedRegex(@"var\(--([\w-]+)(?:,\s*([^)]+))?\)")]
    private static partial Regex VarPattern();

    [GeneratedRegex(@"color-mix\(in srgb,\s*(var\(--[\w-]+\)|#[0-9a-fA-F]{6})\s+(\d+(?:\.\d+)?)%,\s*(var\(--[\w-]+\)|#[0-9a-fA-F]{6})\)")]
    private static partial Regex MixPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"([\w-]+)\s*:\s*([^;]+)")]
    private static partial Regex DeclarationPattern();
}
