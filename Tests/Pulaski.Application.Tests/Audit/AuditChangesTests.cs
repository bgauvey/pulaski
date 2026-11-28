using System.Text.Json;
using System.Text.RegularExpressions;
using Pulaski.Application.Audit;

namespace Pulaski.Application.Tests.Audit;

/// <summary>The audit trail reads an event's data as what changed (#129).</summary>
public partial class AuditChangesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Before_and_after_give_only_the_fields_that_differ_by_value_nested_ones_by_path()
    {
        var data = Json("""{"before":{"code":"EXT-01","rate":500.000,"crew":2.00,"limits":{"lsl":3.2,"usl":3.4},"note":null},"after":{"code":"EXT-01","rate":550,"crew":2,"limits":{"lsl":3.2,"usl":3.5},"note":"Trial"}}""");
        Assert.Equal([("rate", "500", "550"), ("limits.usl", "3.4", "3.5"), ("note", null, "Trial")],
            AuditChanges.Diff(data).Select(c => (c.Field, c.Before, c.After)));
        Assert.Equal("EXT-01", AuditChanges.Label(data));

        // Added (after only), or nothing like before/after: the data as fields.
        Assert.Empty(AuditChanges.Diff(Json("""{"after":{"code":"X"}}""")));
        Assert.Equal([("code", "X")], AuditChanges.Fields(Json("""{"after":{"code":"X"}}""")).Select(f => (f.Field, f.Value)));
        Assert.Equal([("lotNumber", "L-1"), ("qty", "5"), ("tags", "[\"a\"]")],
            AuditChanges.Fields(Json("""{"lotNumber":"L-1","qty":5,"tags":["a"]}""")).Select(f => (f.Field, f.Value)));
        Assert.Equal("L-1", AuditChanges.Label(Json("""{"lotNumber":"L-1"}""")));
        Assert.Null(AuditChanges.Label(Json("""{"qty":5}""")));
    }

    [Fact]
    public void An_event_type_reads_as_its_action_and_a_cursor_round_trips()
    {
        Assert.Equal("capability changed", AuditChanges.Action("pulaski.capability.changed.v1"));
        Assert.Equal("ncr action done", AuditChanges.Action("pulaski.ncr.action_done.v1"));
        Assert.Equal("other.thing", AuditChanges.Action("other.thing"));
        Assert.Equal("terminal enrollment code issued", AuditChanges.Action("pulaski.terminal.enrolment_code_issued.v1"));
        Assert.Equal("job canceled", AuditChanges.Action("pulaski.job.cancelled.v1"));
        Assert.Equal("spc alert signaled", AuditChanges.Action("pulaski.spc_alert.signalled.v1"));

        var row = new AuditRow(Guid.CreateVersion7(), new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero), default, "t", "s", Guid.Empty, "lot", "user", "u", null, null, "{}");
        Assert.Equal((row.RecordedAt, row.Id), AuditCursor.Parse(AuditCursor.Of(row)));
        Assert.Null(AuditCursor.Parse("nonsense"));
    }

    [Fact]
    public void Every_subject_type_the_code_records_is_an_area_to_choose()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "src", "Pulaski.Application")))
        {
            root = root.Parent!;
        }

        var used = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => SubjectType().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(used);
        Assert.Empty(used.Except(Contracts.Audit.AuditAreas.All));
    }

    [GeneratedRegex(@"events\.Create\(\s*EventTypes\.\w+,\s*""([a-z_]+)""")]
    private static partial Regex SubjectType();
}
