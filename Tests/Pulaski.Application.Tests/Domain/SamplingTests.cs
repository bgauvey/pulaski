using Pulaski.Domain.Quality;

namespace Pulaski.Application.Tests.Domain;

/// <summary>A run's due samples: at its start, every so many hours or units, with grace before overdue (#136).</summary>
public class SamplingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 6, 0, 0, TimeSpan.Zero);

    private static SampleDue Due(SamplingPlan plan, double hoursIn, DateTimeOffset[] samples, params (double Hours, decimal Qty)[] output) =>
        SamplingSchedule.Evaluate(plan, Start, null, Start.AddHours(hoursIn), [.. output.Select(o => (Start.AddHours(o.Hours), o.Qty))], samples);

    [Fact]
    public void A_start_sample_is_due_at_once_overdue_after_its_grace_and_done_once_taken()
    {
        var plan = new SamplingPlan(true, null, null, 15);
        Assert.Equal(("due", Start), (Due(plan, 0.1, []).Status, Due(plan, 0.1, []).DueAt!.Value));
        Assert.Equal("overdue", Due(plan, 0.5, []).Status);
        Assert.Equal("done", Due(plan, 0.5, [Start.AddMinutes(20)]).Status);
        // Up to the grace early still counts.
        Assert.Equal("done", Due(plan, 0.5, [Start.AddMinutes(-10)]).Status);
    }

    [Fact]
    public void Hourly_samples_fall_due_on_the_hour_count_missed_ones_and_say_when_the_next_is()
    {
        var plan = new SamplingPlan(false, 1, null, 15);
        var waiting = Due(plan, 0.5, []);
        Assert.Equal(("upcoming", Start.AddHours(1)), (waiting.Status, waiting.NextAt!.Value));

        // At 3 h 30: due at 1, 2 and 3 h; only the 2 h one taken; the 3 h one is overdue and the 1 h one missed.
        var late = Due(plan, 3.5, [Start.AddHours(2.1)]);
        Assert.Equal(("overdue", Start.AddHours(3), 1, Start.AddHours(4)), (late.Status, late.DueAt!.Value, late.Missed, late.NextAt!.Value));
    }

    [Fact]
    public void Unit_samples_fall_due_when_output_reaches_each_multiple()
    {
        var plan = new SamplingPlan(true, null, 100, 15);
        var due = Due(plan, 2, [Start.AddMinutes(5)], (0.5, 60), (1.5, 60));
        Assert.Equal(("overdue", Start.AddHours(1.5), 80m), (due.Status, due.DueAt!.Value, due.NextAfterUnits!.Value));
        Assert.Equal("done", Due(plan, 2, [Start.AddMinutes(5), Start.AddHours(1.6)], (0.5, 60), (1.5, 60)).Status);
    }

    [Fact]
    public void A_plan_asks_for_hours_or_units_not_both_and_sensible_amounts()
    {
        Assert.Null(new SamplingPlan(true, 2, null).Problem());
        Assert.NotNull(new SamplingPlan(false, 2, 100).Problem());
        Assert.NotNull(new SamplingPlan(false, 0, null).Problem());
        Assert.NotNull(new SamplingPlan(false, null, -5).Problem());
        Assert.NotNull(new SamplingPlan(true, null, null, 900).Problem());
        Assert.True(new SamplingPlan(false, null, null).IsEmpty);
        var item = new SpecItem(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, null, 2, null, false, 0, new SamplingPlan(false, 1, 10));
        Assert.Contains(new SpecDefinition([item]).StructuralProblems(), p => p.Code == "sampling");
    }
}
