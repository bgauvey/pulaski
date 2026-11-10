using Pulaski.Domain;
using Pulaski.Domain.Execution;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Production from machine counters (#27, D-097).</summary>
public class MachineCountsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defective_is_the_scrapped_part_of_processed_and_each_count_is_its_units()
    {
        Assert.Equal((348m, 12m), MachineCounts.Output(150m, 5m, 2.4m));
        Assert.Equal((145m, 5m), MachineCounts.Output(150m, 5m, null));
        Assert.Equal((0m, 8m), MachineCounts.Output(5m, 8m, null));
    }

    [Fact]
    public void A_report_from_the_machine_reaches_past_the_last_one_and_stays_inside_its_run()
    {
        var end = Start.AddHours(8);
        MachineCounts.EnsureCovers(Start.AddHours(2), null, Start, end);
        MachineCounts.EnsureCovers(Start.AddHours(3), Start.AddHours(2), Start, end);
        Assert.Throws<DomainRuleException>(() => MachineCounts.EnsureCovers(Start.AddHours(2), Start.AddHours(2), Start, end));
        Assert.Throws<DomainRuleException>(() => MachineCounts.EnsureCovers(Start.AddHours(-1), null, Start, end));
        Assert.Throws<DomainRuleException>(() => MachineCounts.EnsureCovers(end.AddMinutes(1), null, Start, end));
    }
}
