using Pulaski.Domain;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Domain;

public class WorkOrderStatusTests
{
    [Theory]
    [InlineData(WorkOrderStatus.Planned, WorkOrderStatus.Dispatched)]
    [InlineData(WorkOrderStatus.Dispatched, WorkOrderStatus.Planned)]
    [InlineData(WorkOrderStatus.Dispatched, WorkOrderStatus.Firmed)]
    [InlineData(WorkOrderStatus.Firmed, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.Completed, WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Firmed, WorkOrderStatus.Cancelled)]
    public void Allows_the_plan_dispatch_firm_execute_complete_path(string from, string to) =>
        Assert.True(WorkOrderStatus.CanTransition(from, to));

    [Theory]
    [InlineData(WorkOrderStatus.Firmed, WorkOrderStatus.Dispatched)]
    [InlineData(WorkOrderStatus.Firmed, WorkOrderStatus.Planned)]
    [InlineData(WorkOrderStatus.Dispatched, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Planned, WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderStatus.Cancelled)]
    [InlineData(WorkOrderStatus.Closed, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Cancelled, WorkOrderStatus.Planned)]
    [InlineData(WorkOrderStatus.Planned, WorkOrderStatus.Planned)]
    public void Rejects_other_transitions(string from, string to)
    {
        Assert.False(WorkOrderStatus.CanTransition(from, to));
        Assert.Throws<DomainRuleException>(() => WorkOrderStatus.EnsureCanTransition(from, to));
    }

    [Fact]
    public void Every_status_has_a_transition_entry() =>
        Assert.All(WorkOrderStatus.All, s => Assert.NotNull(WorkOrderStatus.NextStatuses(s)));

    [Fact]
    public void Terminal_statuses_have_no_next_status()
    {
        Assert.Empty(WorkOrderStatus.NextStatuses(WorkOrderStatus.Closed));
        Assert.Empty(WorkOrderStatus.NextStatuses(WorkOrderStatus.Cancelled));
    }

    [Theory]
    [InlineData(WorkOrderStatus.Planned, WorkOrderStatus.Dispatched, true)]
    [InlineData(WorkOrderStatus.Dispatched, WorkOrderStatus.Planned, true)]
    [InlineData(WorkOrderStatus.Dispatched, WorkOrderStatus.Firmed, true)]
    [InlineData(WorkOrderStatus.Dispatched, WorkOrderStatus.Cancelled, false)]
    [InlineData(WorkOrderStatus.Firmed, WorkOrderStatus.InProgress, false)]
    public void Dispatch_undispatch_and_firm_are_planning_transitions(string from, string to, bool planning) =>
        Assert.Equal(planning, WorkOrderStatus.IsPlanningTransition(from, to));
}
