using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Domain;

namespace Pulaski.Application.Tests.EquipmentModel;

public class CreateEquipmentHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly CreateEquipmentHandler _handler;

    public CreateEquipmentHandlerTests() =>
        _handler = new CreateEquipmentHandler(
            _f.Stores.UnitOfWork, _f.Stores.Equipment, _f.Stores.Events, _f.Events, _f.Ids);

    [Fact]
    public async Task Inserts_with_v7_id_appends_created_event_and_commits()
    {
        var (_, site, _) = _f.Stores.Equipment.AddHierarchy();

        var result = await _handler.HandleAsync(Area(site.Id), CancellationToken.None);

        var saved = _f.Stores.Equipment.Find(result.Id)!.Value;
        Assert.Equal(7, saved.Id.Version);
        Assert.Equal("EXT", saved.Code);
        Assert.Equal(site.Id, saved.SiteId);

        var evt = Assert.Single(_f.Stores.Events.Appended);
        Assert.Equal(EventTypes.EquipmentCreated, evt.Type);
        Assert.Equal(saved.Id, evt.Subject);
        Assert.Equal("test-user", evt.ActorId);
        Assert.Equal(HandlerFixture.Now, evt.OccurredAt);
        Assert.NotEqual(saved.Id, evt.Id);
        Assert.Equal(1, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Missing_parent_is_a_rule_violation_and_nothing_commits()
    {
        await Assert.ThrowsAsync<DomainRuleException>(
            () => _handler.HandleAsync(Area(Guid.CreateVersion7()), CancellationToken.None));

        Assert.Empty(_f.Stores.Events.Appended);
        Assert.Equal(0, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Child_above_its_parent_is_a_rule_violation()
    {
        var (_, _, line) = _f.Stores.Equipment.AddHierarchy();

        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(Area(line.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_role_not_allowed_at_level()
    {
        var command = Area(Guid.CreateVersion7()) with { EquipmentRole = "machine" };

        var ex = await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(command, CancellationToken.None));

        Assert.Contains(nameof(command.EquipmentRole), ex.Errors.Keys);
        Assert.Equal(0, _f.Stores.UnitOfWork.Begun);
    }

    [Fact]
    public async Task Requires_known_time_zone_on_site()
    {
        var blank = Area(Guid.CreateVersion7()) with { Level = "site", EquipmentRole = "site", TimeZone = " " };
        var unknown = blank with { TimeZone = "Mars/Olympus" };

        var ex = await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(blank, CancellationToken.None));
        Assert.Contains(nameof(blank.TimeZone), ex.Errors.Keys);
        await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(unknown, CancellationToken.None));
    }

    [Fact]
    public async Task Enterprise_has_no_parent_and_everything_else_needs_one()
    {
        var enterpriseWithParent = Area(Guid.CreateVersion7()) with { Level = "enterprise", EquipmentRole = "enterprise" };
        var orphan = Area(parentId: null);

        await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(enterpriseWithParent, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(orphan, CancellationToken.None));
    }

    private static CreateEquipmentCommand Area(Guid? parentId) => new(
        parentId, " EXT ", "Extrusion", null, "area", "area",
        TracksState: false, IsSchedulable: false, HoldsInventory: false, TimeZone: null);
}
