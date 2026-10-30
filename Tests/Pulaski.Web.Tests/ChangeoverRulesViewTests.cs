using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Tests;

/// <summary>Changeover rules on screen (D-074).</summary>
public class ChangeoverRulesViewTests
{
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();

    private static ChangeoverRuleDto R(Guid? line, string? fromMaterial, string? toClass, bool active = true) => new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = Guid.Empty,
        EquipmentId = line,
        EquipmentCode = line is null ? null : "L1",
        FromMaterialCode = fromMaterial,
        ToMaterialClassCode = toClass,
        DurationMinutes = 90,
        IsActive = active,
    };

    [Fact]
    public void A_rule_reads_as_line_from_and_to_and_its_time()
    {
        var rule = R(null, "WH", "PROFILES");
        Assert.Equal(("All lines", "WH", "Class PROFILES"), (ChangeoverRulesView.Line(rule), ChangeoverRulesView.From(rule), ChangeoverRulesView.To(rule)));
        Assert.Equal(("45 min", "2 h", "1 h 30 min"), (ChangeoverRulesView.Duration(45), ChangeoverRulesView.Duration(120), ChangeoverRulesView.Duration(90)));
    }

    [Fact]
    public void Filtering_by_line_keeps_the_rules_for_every_line_and_finds_a_side()
    {
        var rules = new[] { R(null, "WH", null), R(Line, "BK", "PROFILES"), R(Other, "WH", null), R(Line, "RD", null, active: false) };

        Assert.Equal(["WH", "BK"], ChangeoverRulesView.Filter(rules, Line, null, includeOff: false).Select(r => r.FromMaterialCode));
        Assert.Equal(3, ChangeoverRulesView.Filter(rules, Line, null, includeOff: true).Count);
        Assert.Equal(["BK"], ChangeoverRulesView.Filter(rules, null, "prof", includeOff: false).Select(r => r.FromMaterialCode));
    }

    [Fact]
    public void The_form_needs_each_side_chosen_a_real_change_and_minutes_within_a_week()
    {
        var white = Guid.CreateVersion7();
        Assert.Null(ChangeoverRulesView.Problem(ChangeoverRulesView.Anything, null, ChangeoverRulesView.Material, white, 30));
        Assert.NotNull(ChangeoverRulesView.Problem(ChangeoverRulesView.Material, null, ChangeoverRulesView.Anything, null, 30));
        Assert.NotNull(ChangeoverRulesView.Problem(ChangeoverRulesView.Material, white, ChangeoverRulesView.Material, white, 30));
        Assert.NotNull(ChangeoverRulesView.Problem(ChangeoverRulesView.Anything, null, ChangeoverRulesView.Anything, null, ChangeoverRulesView.MaxMinutes + 1));
        Assert.NotNull(ChangeoverRulesView.Problem(ChangeoverRulesView.Anything, null, ChangeoverRulesView.Anything, null, null));
    }
}
