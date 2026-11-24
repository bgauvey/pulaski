using Pulaski.Web.Services;
using Pulaski.Contracts.Localization;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Tests;

/// <summary>
/// The term catalog (#128) is exactly the terms the web's source translates. When it isn't, run the tests with PULASKI_UPDATE_TERMS=1 to rewrite
/// <c>src/Pulaski.Contracts/Localization/terms.txt</c>, then add translations on Configure → Languages.
/// </summary>
public class TermCatalogTests
{
    private static readonly string[] ServerProjects = ["Pulaski.Domain", "Pulaski.Application", "Pulaski.Api", "Pulaski.Infrastructure"];

    [Fact]
    public void The_catalog_is_every_term_the_web_translates()
    {
        var root = Root();
        var web = Path.Combine(root, "src", "Pulaski.Web");
        var found = SourceFiles(web, ".razor", ".cs")
            .Where(f => !f.EndsWith("TermExtractor.cs", StringComparison.Ordinal))
            .SelectMany(f => TermExtractor.Terms(File.ReadAllText(f)));
        var messages = ServerProjects
            .SelectMany(project => SourceFiles(Path.Combine(root, "src", project), ".cs"))
            .SelectMany(f => TermExtractor.Messages(File.ReadAllText(f)));
        var catalog = TermExtractor.Catalog(found.Concat(messages));

        var path = Path.Combine(root, "src", "Pulaski.Contracts", "Localization", "terms.txt");
        if (Environment.GetEnvironmentVariable("PULASKI_UPDATE_TERMS") == "1")
        {
            File.WriteAllText(path, catalog);
            return;
        }

        Assert.Equal(catalog, TermExtractor.Catalog(TermCatalog.Terms));
    }

    [Fact]
    public void Terms_are_found_in_indexers_and_marks_with_their_quotes()
    {
        const string Source = """
            <h1>@L["Sign in"]</h1> @L["Run {0} ended", run] new("x", Term.Mark("Work orders")) L[group.Label] @L["Say \"hello\""]
            """;
        Assert.Equal(["Sign in", "Run {0} ended", "Work orders", "Say \"hello\""], TermExtractor.Terms(Source));
    }

    [Fact]
    public void Server_messages_are_read_as_their_builder_makes_them()
    {
        const string Source = """
            throw new DomainRuleException($"{line.Code} can't run {material.Code}.");
            throw new NotFoundException("No such job.");
            throw new ConflictException($"{n:N0} left, {Name(a, b)} at {when.ToString("HH:mm")}; {{braces}} {(x ? "a" : "b"),5}");
            throw new DomainRuleException(IsExact ? "One" : "Other");
            throw new DomainRuleException($"Part " + rest);
            throw new ForbiddenException(
                $"Not at {site}.");
            throw new DomainRuleException(
                $"{a} is more " +
                $"than {b}.");
            throw new DomainRuleException("Plain " + "parts.");
            throw new DomainRuleException("Mixed " + $"{parts}.");
            """;
        Assert.Equal(
            ["{0} can't run {1}.", "No such job.", "{0:N0} left, {1} at {2}; {{braces}} {3,5}", "Not at {0}.", "{0} is more than {1}.", "Plain parts."],
            TermExtractor.Messages(Source));
    }

    [Fact]
    public void Field_checks_are_read_from_their_message_argument()
    {
        const string Source = """
            new Validator()
                .Require(command.Lines.All(l => l.Qty is > 0 and < 1_000), nameof(command.Lines), "Each line needs a quantity.")
                .Require(Pick(a, "x, y", 'z') != null, "Name", $"At most {max} characters, not {Len(s, 2):N0}.")
                .Require(ok, "Note", problem)
                .RequireText(command.Code, nameof(command.Code), 50);
            throw ValidationException.Field("Body", "Write what the next shift should know.");
            throw ValidationException.Field(nameof(query.Status), $"Unknown status '{query.Status}'.");
            throw ValidationException.Field("Settings", problems);
            """;
        Assert.Equal(
            ["Each line needs a quantity.", "At most {0} characters, not {1:N0}.", "Write what the next shift should know.", "Unknown status '{0}'."],
            TermExtractor.Messages(Source));
    }

    [Fact]
    public void Notes_and_coded_warnings_are_read_from_their_message_argument()
    {
        const string Source = """
            notes.Add(Notes.Of($"{job.JobNumber} on {line} can start now."));
            warnings.Add(Notes.Inventory("feed_shortfall", $"{qty:N0} wasn't consumed."));
            problems.Add(new("unknown_component", $"Line {item.LineNo} names a material that doesn't exist.", item.Id));
            warnings.Add(new ScheduleWarning(ScheduleWarningCode.Late, $"Projected to finish {late} after the due date."));
            return UserMessage.From($"{name} isn't qualified for {line}.");
            list.Add(new("x", 1));
            notes.Add(Notes.Of(text));
            """;
        Assert.Equal(
            ["{0} on {1} can start now.", "{0:N0} wasn't consumed.", "Line {0} names a material that doesn't exist.", "Projected to finish {0} after the due date.", "{0} isn't qualified for {1}."],
            TermExtractor.Messages(Source));
    }

    [Fact]
    public void Every_status_and_kind_code_in_the_schema_has_its_label_as_a_term()
    {
        // Every CHECK of the form "column IN (...)" or "(a, b) IN ((...), ...)", as LabelTerms was made from.
        var schema = File.ReadAllText(Path.Combine(Root(), "db", "schema-draft.sql"));
        var codes = new List<string>();
        foreach (System.Text.RegularExpressions.Match check in System.Text.RegularExpressions.Regex.Matches(schema, @"CHECK\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var start = check.Index + check.Length - 1;
            var (depth, end) = (0, start);
            for (; end < schema.Length; end++)
            {
                depth += schema[end] switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth == 0)
                {
                    break;
                }
            }

            var body = schema[(start + 1)..end];
            var head = System.Text.RegularExpressions.Regex.Match(body, @"^\s*(\(?[\w\s,]+\)?)\s+IN\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (head.Success && !System.Text.RegularExpressions.Regex.IsMatch(head.Groups[1].Value, @"\b(content_type|protocol)\b"))
            {
                codes.AddRange(System.Text.RegularExpressions.Regex.Matches(body, "'([^']+)'").Select(v => v.Groups[1].Value).Where(v => !v.Contains('/', StringComparison.Ordinal)));
            }
        }

        Assert.Contains("storage_zone", codes);
        Assert.All(codes, code => Assert.Contains(Display.Label(code), LabelTerms.All));
    }

    private static IEnumerable<string> SourceFiles(string directory, params string[] extensions) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
