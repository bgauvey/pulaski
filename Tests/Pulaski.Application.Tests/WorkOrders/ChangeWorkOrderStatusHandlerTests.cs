using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.WorkOrders;

public class ChangeWorkOrderStatusHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly ChangeWorkOrderStatusHandler _handler;

    public ChangeWorkOrderStatusHandlerTests() =>
        _handler = new ChangeWorkOrderStatusHandler(_f.Stores.UnitOfWork, _f.Stores.WorkOrders, _f.Stores.Events, _f.Events);

    [Fact]
    public async Task Releases_a_planned_work_order_and_records_the_reason()
    {
        var wo = _f.Stores.WorkOrders.Add(WorkOrderStatus.Planned, version: 10);

        var result = await _handler.HandleAsync(
            new ChangeWorkOrderStatusCommand(wo.Id, 10, WorkOrderStatus.Released, "Material staged"), CancellationToken.None);

        Assert.Equal(11, result.Version);
        Assert.Equal(WorkOrderStatus.Released, _f.Stores.WorkOrders.Find(wo.Id)!.Value.Status);
        var evt = Assert.Single(_f.Stores.Events.Appended);
        Assert.Equal(EventTypes.WorkOrderStatusChanged, evt.Type);
        Assert.Equal("Material staged", evt.Reason);
        Assert.Equal(1, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Stale_version_is_a_conflict_even_when_the_transition_is_invalid()
    {
        var wo = _f.Stores.WorkOrders.Add(WorkOrderStatus.Planned, version: 10);

        await Assert.ThrowsAsync<ConflictException>(() => _handler.HandleAsync(
            new ChangeWorkOrderStatusCommand(wo.Id, 9, WorkOrderStatus.Closed, null), CancellationToken.None));
        Assert.Empty(_f.Stores.Events.Appended);
        Assert.Equal(0, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Invalid_transition_is_a_rule_violation()
    {
        var wo = _f.Stores.WorkOrders.Add(WorkOrderStatus.Planned, version: 10);

        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(
            new ChangeWorkOrderStatusCommand(wo.Id, 10, WorkOrderStatus.Closed, null), CancellationToken.None));
        Assert.Empty(_f.Stores.Events.Appended);
    }

    [Fact]
    public async Task Unknown_status_is_a_validation_error() =>
        await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(
            new ChangeWorkOrderStatusCommand(Guid.CreateVersion7(), 1, "on_fire", null), CancellationToken.None));

    [Fact]
    public async Task Missing_work_order_is_not_found() =>
        await Assert.ThrowsAsync<NotFoundException>(() => _handler.HandleAsync(
            new ChangeWorkOrderStatusCommand(Guid.CreateVersion7(), 1, WorkOrderStatus.Released, null), CancellationToken.None));

    [Fact]
    public async Task List_fills_next_statuses()
    {
        _f.Stores.WorkOrders.Add(WorkOrderStatus.Released);
        var list = new ListWorkOrdersHandler(_f.Stores.WorkOrders);

        var rows = await list.HandleAsync(new ListWorkOrdersQuery(null, null), CancellationToken.None);

        Assert.Equal(WorkOrderStatus.NextStatuses(WorkOrderStatus.Released), Assert.Single(rows).NextStatuses);
    }
}
