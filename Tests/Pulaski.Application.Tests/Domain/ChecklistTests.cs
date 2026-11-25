using Pulaski.Domain;
using Pulaski.Domain.Instructions;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Setup and first-article checklists are checked as written and judged as answered (#126c).</summary>
public class ChecklistTests
{
    private static readonly Checklist Setup = Checklists.Check([new Checklist("setup",
    [
        new ChecklistItem(" Guard in place? ", "yes_no", true, Expected: true),
        new ChecklistItem("Die temperature", "number", true, Min: 180, Max: 220, Unit: " °C "),
        new ChecklistItem("Colour", "choice", false, Choices: ["Black", " Grey ", "Black", ""]),
        new ChecklistItem("Remarks", "text", false),
    ])])[0];

    [Fact]
    public void Checklists_are_cleaned_up_one_per_phase_setup_first_and_empty_ones_left_out()
    {
        Assert.Equal(("Guard in place?", "°C"), (Setup.Items[0].Prompt, Setup.Items[1].Unit));
        Assert.Equal(["Black", "Grey"], Setup.Items[2].Choices);

        var both = Checklists.Check([new Checklist("first_article", [new ChecklistItem("Look", "text", true)]), new Checklist("setup", [new ChecklistItem("Clean", "yes_no", true)])]);
        Assert.Equal(["setup", "first_article"], both.Select(c => c.Phase));
        Assert.Empty(Checklists.Check([new Checklist("setup", [])]));
    }

    [Theory]
    [InlineData("teardown", "text", null, null)]
    [InlineData("setup", "photo", null, null)]
    [InlineData("setup", "number", 5, 1)]
    public void A_checklist_that_isnt_right_is_refused(string phase, string kind, int? min, int? max) =>
        Assert.Throws<DomainRuleException>(() => Checklists.Check([new Checklist(phase, [new ChecklistItem("Q", kind, true, Min: min, Max: max)])]));

    [Fact]
    public void A_phase_comes_once_and_a_choice_needs_two_choices()
    {
        Assert.Throws<DomainRuleException>(() => Checklists.Check([new Checklist("setup", [new ChecklistItem("A", "text", true)]), new Checklist("setup", [new ChecklistItem("B", "text", true)])]));
        Assert.Throws<DomainRuleException>(() => Checklists.Check([new Checklist("setup", [new ChecklistItem("C", "choice", true, Choices: ["Only"])])]));
        Assert.Throws<DomainRuleException>(() => Checklists.Check([new Checklist("setup", [new ChecklistItem(" ", "text", true)])]));
    }

    [Fact]
    public void Answers_within_what_each_item_allows_pass_and_others_fail_the_checklist()
    {
        var ok = Checklists.Evaluate(Setup, new Dictionary<int, string?> { [1] = "Yes", [2] = "200", [3] = "Grey", [4] = " " });
        Assert.True(ok.Passed);
        Assert.Equal([(1, "yes"), (2, "200"), (3, "Grey")], ok.Answers.Select(a => (a.Item, a.Value!)));

        var bad = Checklists.Evaluate(Setup, new Dictionary<int, string?> { [1] = "no", [2] = "221" });
        Assert.False(bad.Passed);
        Assert.Equal([1, 2], bad.Answers.Where(a => !a.Ok).Select(a => a.Item));
    }

    [Fact]
    public void A_required_item_must_be_answered_and_answers_must_be_ones_the_item_takes()
    {
        Assert.Throws<DomainRuleException>(() => Checklists.Evaluate(Setup, new Dictionary<int, string?> { [1] = "yes" }));
        Assert.Throws<DomainRuleException>(() => Checklists.Evaluate(Setup, new Dictionary<int, string?> { [1] = "maybe", [2] = "200" }));
        Assert.Throws<DomainRuleException>(() => Checklists.Evaluate(Setup, new Dictionary<int, string?> { [1] = "yes", [2] = "hot" }));
        Assert.Throws<DomainRuleException>(() => Checklists.Evaluate(Setup, new Dictionary<int, string?> { [1] = "yes", [2] = "200", [3] = "Red" }));
    }

    [Fact]
    public void A_version_with_only_a_checklist_releases_and_the_next_draft_keeps_it()
    {
        var draft = InstructionVersion.FirstDraft(Guid.CreateVersion7(), Guid.CreateVersion7()).SaveDraft("", [Setup]);
        var released = draft.Release(DateTimeOffset.UnixEpoch, "pe", null, files: 0);
        Assert.Single(released.NextDraft(Guid.CreateVersion7()).Checklists);
        Assert.Single(draft.SaveDraft("Text").Checklists);
    }
}
