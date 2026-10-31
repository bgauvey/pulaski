using Pulaski.Application.Scheduling;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Changeover rules made on the screen or imported (D-074): what may be kept, duplicates, and reading a file.</summary>
public class ChangeoverRulesTests
{
    private static readonly Guid White = Guid.CreateVersion7();
    private static readonly Guid Black = Guid.CreateVersion7();
    private static readonly Guid Profiles = Guid.CreateVersion7();
    private static readonly Guid Line = Guid.CreateVersion7();

    [Fact]
    public void A_rule_names_each_side_once_changes_material_and_takes_whole_minutes_up_to_a_week()
    {
        var ok = new ChangeoverPattern(null, White, null, null, Profiles);
        Assert.Null(ChangeoverRules.Problem(ok, TimeSpan.FromMinutes(45), null));
        Assert.Null(ChangeoverRules.Problem(new(Line, null, null, null, null), TimeSpan.Zero, "any change"));

        Assert.NotNull(ChangeoverRules.Problem(new(null, White, Profiles, Black, null), TimeSpan.FromMinutes(5), null));
        Assert.NotNull(ChangeoverRules.Problem(new(null, White, null, White, null), TimeSpan.FromMinutes(5), null));
        Assert.NotNull(ChangeoverRules.Problem(ok, TimeSpan.FromDays(8), null));
        Assert.NotNull(ChangeoverRules.Problem(ok, TimeSpan.FromSeconds(90), null));
        Assert.NotNull(ChangeoverRules.Problem(ok, TimeSpan.FromMinutes(5), new string('x', 501)));
    }

    [Fact]
    public void Only_one_rule_with_a_pattern_is_on_at_a_time()
    {
        var pattern = new ChangeoverPattern(Line, White, null, Black, null);
        var on = new ChangeoverRuleRecord(Guid.CreateVersion7(), Guid.Empty, pattern, TimeSpan.FromMinutes(30), null, true);
        var off = on with { Id = Guid.CreateVersion7(), IsActive = false };

        Assert.Equal(on, ChangeoverRules.Duplicate([on, off], pattern));
        Assert.Null(ChangeoverRules.Duplicate([on, off], pattern, exceptId: on.Id));
        Assert.Null(ChangeoverRules.Duplicate([on], pattern with { EquipmentId = null }));
    }

    [Fact]
    public void A_file_names_its_columns_and_each_row_needs_whole_minutes()
    {
        var source = ChangeoverRuleCsv.Parse("Line,From,To_Class,Minutes,Active\nL1,WH,PROFILES,45,\n,,, 1.5,maybe\n");

        Assert.Empty(source.Problems);
        var (first, second) = (source.Rows[0], source.Rows[1]);
        Assert.Equal(("L1", "WH", "PROFILES", 45, (bool?)null), (first.LineCode, first.FromMaterial, first.ToClass, first.Minutes, first.IsActive));
        Assert.Equal("WH → class PROFILES on L1", first.Label);
        Assert.Null(first.Note);
        Assert.Equal(2, second.Problems.Count);

        Assert.Contains("The required column 'minutes' is missing.", ChangeoverRuleCsv.Parse("line,from\nL1,WH\n").Problems);
        Assert.Contains(ChangeoverRuleCsv.Parse("line,minutes,colour\n").Problems, p => p.StartsWith("Unknown column 'colour'", StringComparison.Ordinal));
        Assert.Equal(Contracts.Scheduling.ChangeoverRuleCsvFormat.Header, ChangeoverRuleCsv.Template);
    }
}
