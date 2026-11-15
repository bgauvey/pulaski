using Pulaski.Domain;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Nelson's run rules (D-099), marked at the point that completes each pattern.</summary>
public class RunRulesTests
{
    // Centre 0, sigma 1: a value is its own z.
    private static List<RulePoint> Points(params double[] values) => [.. values.Select(v => new RulePoint(v, 0, 1))];

    private static int[] Where(IReadOnlyList<IReadOnlyList<int>> found, int rule) =>
        [.. found.Select((rules, i) => (rules, i)).Where(t => t.rules.Contains(rule)).Select(t => t.i)];

    [Fact]
    public void Rule_one_flags_a_point_beyond_three_sigma()
    {
        var found = RunRules.Check(Points(0, 3.5, -3.1, 2.9), RunRules.All);
        Assert.Equal([1, 2], Where(found, 1));
    }

    [Fact]
    public void A_run_of_nine_on_one_side_is_marked_at_the_ninth()
    {
        var found = RunRules.Check(Points(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, -0.5), RunRules.All);
        Assert.Equal([8, 9], Where(found, 2));
    }

    [Fact]
    public void Six_rising_and_fourteen_alternating_are_trends_of_the_values()
    {
        Assert.Equal([5], Where(RunRules.Check(Points(-1, -0.6, -0.2, 0.2, 0.6, 1.0, 0.9), RunRules.All), 3));
        var alternating = Enumerable.Range(0, 14).Select(i => i % 2 == 0 ? 0.2 : -0.2).ToArray();
        Assert.Equal([13], Where(RunRules.Check(Points(alternating), RunRules.All), 4));
    }

    [Fact]
    public void Zone_rules_count_points_beyond_two_and_one_sigma_on_one_side()
    {
        Assert.Equal([2], Where(RunRules.Check(Points(2.5, 0, 2.2), RunRules.All), 5));
        Assert.Empty(Where(RunRules.Check(Points(2.5, 0, -2.2), RunRules.All), 5));
        Assert.Equal([4], Where(RunRules.Check(Points(1.5, 1.2, 0, 1.1, 1.3), RunRules.All), 6));
        Assert.Equal([14], Where(RunRules.Check(Points([.. Enumerable.Repeat(0.3, 15)]), RunRules.All), 7));
        Assert.Equal([7], Where(RunRules.Check(Points(1.5, -1.5, 1.5, -1.5, 1.5, -1.5, 1.5, -1.5), RunRules.All), 8));
    }

    [Fact]
    public void Only_chosen_rules_count_and_a_point_without_limits_breaks_a_run()
    {
        var run = Points(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5);
        Assert.Empty(Where(RunRules.Check(run, [1]), 2));
        run[4] = new RulePoint(0.5, null, null);
        Assert.Empty(Where(RunRules.Check(run, RunRules.All), 2));

        // Each point's own centre and sigma: a run spans new limits.
        var spanning = Enumerable.Range(0, 9).Select(i => i < 5 ? new RulePoint(10.5, 10, 1) : new RulePoint(20.4, 20, 2)).ToList();
        Assert.Equal([8], Where(RunRules.Check(spanning, RunRules.All), 2));
    }

    [Fact]
    public void Rule_one_is_always_kept_and_others_are_nelsons()
    {
        Assert.Equal([1, 3], RunRules.Normalize([3, 3]));
        Assert.Equal([1, 2, 5, 6], RunRules.Default);
        Assert.Throws<DomainRuleException>(() => RunRules.Normalize([9]));
    }
}
