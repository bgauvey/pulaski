using Pulaski.Domain;
using Pulaski.Domain.Setup;

namespace Pulaski.Application.Tests.Domain;

/// <summary>The setup checklist follows the guide's order and is ready once every step a first job needs is done (#139).</summary>
public class SetupChecklistTests
{
    private static readonly string[] Required =
        [SetupSteps.Site, SetupSteps.Lines, SetupSteps.Materials, SetupSteps.Capabilities, SetupSteps.Processes, SetupSteps.Locations, SetupSteps.WorkOrders, SetupSteps.FirstJob];

    [Fact]
    public void Steps_are_done_by_what_is_there_and_ready_when_every_required_one_is()
    {
        Assert.Equal(Required, SetupSteps.All.Where(s => s.Required).Select(s => s.Key));
        var counts = Required.Where(k => k != SetupSteps.FirstJob).ToDictionary(k => k, _ => 2);
        var steps = SetupChecklist.Evaluate(new SetupFacts(counts), new HashSet<string>());
        Assert.False(SetupChecklist.Ready(steps));
        Assert.Equal((SetupStatus.Done, 2), (steps[0].Status, steps[0].Count));
        Assert.Equal(SetupStatus.ToDo, steps.Single(s => s.Step.Key == SetupSteps.FirstJob).Status);

        counts[SetupSteps.FirstJob] = 1;
        Assert.True(SetupChecklist.Ready(SetupChecklist.Evaluate(new SetupFacts(counts), new HashSet<string>())));
    }

    [Fact]
    public void Only_optional_steps_can_be_marked_not_needed_and_done_beats_the_mark()
    {
        var steps = SetupChecklist.Evaluate(new SetupFacts(new Dictionary<string, int> { [SetupSteps.Units] = 3 }),
            new HashSet<string> { SetupSteps.Shifts, SetupSteps.Units, SetupSteps.Lines });
        Assert.Equal(SetupStatus.NotNeeded, steps.Single(s => s.Step.Key == SetupSteps.Shifts).Status);
        Assert.Equal(SetupStatus.Done, steps.Single(s => s.Step.Key == SetupSteps.Units).Status);
        Assert.Equal(SetupStatus.ToDo, steps.Single(s => s.Step.Key == SetupSteps.Lines).Status);

        Assert.Equal(SetupSteps.Shifts, SetupChecklist.Skippable(SetupSteps.Shifts).Key);
        Assert.Throws<DomainRuleException>(() => SetupChecklist.Skippable(SetupSteps.Lines));
        Assert.Throws<DomainRuleException>(() => SetupChecklist.Skippable("nonsense"));
    }
}
