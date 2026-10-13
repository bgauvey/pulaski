using Pulaski.Application.Abstractions;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.WorkOrders;

public class CreateWorkOrderHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly CreateWorkOrderHandler _handler;

    public CreateWorkOrderHandlerTests() =>
        _handler = new CreateWorkOrderHandler(
            _f.Stores.UnitOfWork, _f.Stores.WorkOrders, _f.Stores.Equipment, _f.Stores.Materials,
            _f.Stores.Events, _f.Events, _f.Ids);

    [Fact]
    public async Task Creates_planned_local_order_with_event()
    {
        var (_, site, line) = _f.Stores.Equipment.AddHierarchy();
        var material = _f.Stores.Materials.AddActive();

        var result = await _handler.HandleAsync(Command(site.Id, material, line.Id), CancellationToken.None);

        var saved = _f.Stores.WorkOrders.Find(result.Id)!.Value;
        Assert.Equal(WorkOrderStatus.Planned, saved.Status);
        Assert.Equal(WorkOrder.LocalOrigin, saved.Origin);
        Assert.Equal(EventTypes.WorkOrderCreated, Assert.Single(_f.Stores.Events.Appended).Type);
        Assert.Equal(1, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Site_must_be_a_site()
    {
        var (_, _, line) = _f.Stores.Equipment.AddHierarchy();

        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(
            Command(line.Id, _f.Stores.Materials.AddActive(), null), CancellationToken.None));
    }

    [Fact]
    public async Task Material_must_be_active()
    {
        var (_, site, _) = _f.Stores.Equipment.AddHierarchy();

        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(
            Command(site.Id, Guid.CreateVersion7(), null), CancellationToken.None));
    }

    [Fact]
    public async Task Target_must_be_inside_the_site()
    {
        var (_, site, _) = _f.Stores.Equipment.AddHierarchy();
        var (_, _, otherLine) = _f.Stores.Equipment.AddHierarchy();

        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(
            Command(site.Id, _f.Stores.Materials.AddActive(), otherLine.Id), CancellationToken.None));
        Assert.Equal(0, _f.Stores.UnitOfWork.Committed);
    }

    private static CreateWorkOrderCommand Command(Guid siteId, Guid materialId, Guid? targetId) => new(
        siteId, "WO-1", materialId, 500, Guid.CreateVersion7(), targetId, null, null, null, 0);
}
