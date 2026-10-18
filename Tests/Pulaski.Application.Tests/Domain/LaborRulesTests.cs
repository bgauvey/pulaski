using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Labor sessions and how their hours are shared out (D-040).</summary>
public class LaborRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid JobA = Guid.CreateVersion7();
    private static readonly Guid JobB = Guid.CreateVersion7();

    private static LaborSpan Span(string type, double fromHours, double? toHours, decimal pct = 100, Guid? job = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Line, job, type, T0.AddHours(fromHours), toHours is { } t ? T0.AddHours(t) : null, pct);

    [Fact]
    public void Direct_time_counts_towards_whatever_ran_on_the_line_and_the_rest_is_idle()
    {
        RunSpan[] runs = [new(Guid.CreateVersion7(), JobA, Line, T0.AddHours(1), T0.AddHours(3)), new(Guid.CreateVersion7(), JobB, Line, T0.AddHours(4), T0.AddHours(8))];

        var shares = LaborRules.Attribute([Span(LaborTypes.Direct, 0, 8)], runs, T0.AddHours(10));

        Assert.Equal(2m, shares.Where(s => s.JobId == JobA).Sum(s => s.Hours));
        Assert.Equal(4m, shares.Where(s => s.JobId == JobB).Sum(s => s.Hours));
        Assert.Equal(2m, Assert.Single(shares, s => s.RunId is null).Hours);
    }

    [Fact]
    public void A_share_of_time_and_a_named_job_narrow_what_counts()
    {
        RunSpan[] runs = [new(Guid.CreateVersion7(), JobA, Line, T0, T0.AddHours(4)), new(Guid.CreateVersion7(), JobB, Line, T0.AddHours(4), null)];

        var half = LaborRules.Attribute([Span(LaborTypes.Direct, 0, 8, 50, JobB)], runs, T0.AddHours(8));
        Assert.Equal(2m, half.Where(s => s.JobId == JobB).Sum(s => s.Hours));
        Assert.DoesNotContain(half, s => s.JobId == JobA);

        // Setup counts towards its job even with nothing running; indirect towards no job; an open session runs until now.
        var setup = LaborRules.Attribute([Span(LaborTypes.Setup, 8, 9, job: Guid.CreateVersion7())], [], T0.AddHours(10));
        Assert.NotNull(Assert.Single(setup).JobId);
        var open = LaborRules.Attribute([Span(LaborTypes.Indirect, 0, null)], runs, T0.AddHours(3));
        Assert.Equal((3m, (Guid?)null), (Assert.Single(open).Hours, open[0].JobId));
    }

    [Fact]
    public void Efficiency_is_standard_labor_over_direct_hours()
    {
        Assert.Equal(80m, LaborRules.Efficiency(8, 2, 20));
        Assert.Null(LaborRules.Efficiency(8, null, 20));
        Assert.Null(LaborRules.Efficiency(8, 2, 0));
    }

    [Fact]
    public void Sessions_are_checked_and_a_person_can_be_split_but_not_beyond_all_their_time()
    {
        var now = T0.AddHours(10);
        Assert.NotNull(LaborRules.Problem(LaborTypes.Indirect, null, null, T0, null, 100, now));
        Assert.NotNull(LaborRules.Problem(LaborTypes.Setup, null, null, T0, null, 100, now));
        Assert.NotNull(LaborRules.Problem(LaborTypes.Direct, null, null, now.AddHours(1), null, 100, now));
        Assert.NotNull(LaborRules.Problem(LaborTypes.Direct, null, null, T0.AddHours(2), T0.AddHours(1), 100, now));
        Assert.NotNull(LaborRules.Problem(LaborTypes.Direct, null, null, T0, null, 0, now));
        Assert.Null(LaborRules.Problem(LaborTypes.Direct, null, null, T0, null, 100, now));

        Assert.Null(LaborRules.AllocationProblem([50m], 50m));
        Assert.NotNull(LaborRules.AllocationProblem([50m, 40m], 20m));
    }

    [Fact]
    public void Work_belongs_to_the_shift_running_or_starting_soon_with_its_crew()
    {
        var schedule = new ShiftSchedule("2X8", "Two shifts", 7, new DateOnly(2026, 10, 19),
        [
            new("A", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4], "RED"),
            new("B", "Lates", new TimeOnly(14, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4], "BLUE"),
        ]);

        Assert.Equal(("A", "RED"), (schedule.ShiftAt(TimeZoneInfo.Utc, T0.AddHours(1), LaborRules.ShiftLead)!.Code, schedule.ShiftAt(TimeZoneInfo.Utc, T0.AddHours(1), LaborRules.ShiftLead)!.Crew));
        Assert.Equal("A", schedule.ShiftAt(TimeZoneInfo.Utc, T0.AddMinutes(-30), LaborRules.ShiftLead)!.Code);
        Assert.Equal("B", schedule.ShiftAt(TimeZoneInfo.Utc, T0.AddHours(8), LaborRules.ShiftLead)!.Code);
        Assert.Null(schedule.ShiftAt(TimeZoneInfo.Utc, T0.AddHours(18), LaborRules.ShiftLead));
    }
}
