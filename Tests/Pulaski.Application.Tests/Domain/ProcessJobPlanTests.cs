using Pulaski.Domain;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Turning a released process into a work order's jobs (D-024).</summary>
public class ProcessJobPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

    private static ProcessOperation Op(int number, params decimal[] shares) =>
        new(Guid.CreateVersion7(), number, $"Op {number}", null, 0, 0, [.. shares.Select(s => new OperationEquipment(Guid.CreateVersion7(), s))]);

    private static ProcessDependency Link(ProcessOperation from, ProcessOperation to, string type, int lagMinutes) =>
        new(Guid.CreateVersion7(), from.Id, to.Id, type, TimeSpan.FromMinutes(lagMinutes));

    [Fact]
    public void Shares_round_down_and_the_last_entity_takes_the_remainder()
    {
        var op = Op(10, 33.33m, 33.33m, 33.34m);

        var jobs = ProcessJobPlan.For(new ProcessDefinition([op], []), "WO-7", 10m, Start);

        Assert.Equal([3.333m, 3.333m, 3.334m], jobs.Select(j => j.PlannedQty));
        Assert.Equal(10m, jobs.Sum(j => j.PlannedQty));
        Assert.Equal(["WO-7-10-1", "WO-7-10-2", "WO-7-10-3"], jobs.Select(j => j.JobNumber));
        Assert.Equal(op.Equipment.Select(e => e.EquipmentId), jobs.Select(j => j.EquipmentId));
    }

    [Fact]
    public void Jobs_come_in_flow_order_and_wait_for_the_longest_lag_before_them()
    {
        // 10 → 20 (+30 min, after start) → 40 (+10 min); 10 → 40 (+20 min, after end): 40 waits 40 minutes.
        var (a, b, c) = (Op(10, 100m), Op(20, 100m), Op(40, 100m));
        var definition = new ProcessDefinition(
            [c, b, a],
            [Link(a, b, DependencyType.AfterStart, 30), Link(b, c, DependencyType.AfterStart, 10), Link(a, c, DependencyType.AfterEnd, 20)]);

        var jobs = ProcessJobPlan.For(definition, "WO-8", 500m, Start);

        Assert.Equal(
            [("WO-8-10", Start), ("WO-8-20", Start.AddMinutes(30)), ("WO-8-40", Start.AddMinutes(40))],
            jobs.Select(j => (j.JobNumber, j.EarliestStart)));
        Assert.All(jobs, j => Assert.Equal(500m, j.PlannedQty));
    }

    [Fact]
    public void An_unreleasable_definition_is_refused()
    {
        var unshared = Op(10, 60m);

        Assert.Throws<DomainRuleException>(() => ProcessJobPlan.For(new ProcessDefinition([unshared], []), "WO-9", 1m, Start));
        Assert.Throws<DomainRuleException>(() => ProcessJobPlan.For(ProcessDefinition.Empty, "WO-9", 1m, Start));
    }

    [Fact]
    public void A_quantity_too_small_to_share_is_refused()
    {
        var op = Op(10, 50m, 50m);

        Assert.Throws<DomainRuleException>(() => ProcessJobPlan.For(new ProcessDefinition([op], []), "WO-9", 0.000001m, Start));
    }
}
