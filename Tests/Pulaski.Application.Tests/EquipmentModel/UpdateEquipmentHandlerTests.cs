using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;

namespace Pulaski.Application.Tests.EquipmentModel;

public class UpdateEquipmentHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly UpdateEquipmentHandler _handler;

    public UpdateEquipmentHandlerTests() =>
        _handler = new UpdateEquipmentHandler(_f.Stores.UnitOfWork, _f.Stores.Equipment, _f.Stores.Events, _f.Events);

    [Fact]
    public async Task Updates_and_records_before_and_after()
    {
        var (_, _, line) = _f.Stores.Equipment.AddHierarchy();

        var result = await _handler.HandleAsync(
            new UpdateEquipmentCommand(line.Id, 1, "Renamed", null, true, false, false, true), CancellationToken.None);

        Assert.Equal(2, result.Version);
        Assert.Equal("Renamed", _f.Stores.Equipment.Find(line.Id)!.Value.Name);
        var evt = Assert.Single(_f.Stores.Events.Appended);
        Assert.Equal(EventTypes.EquipmentUpdated, evt.Type);
        Assert.Contains("Before", evt.Data.GetType().GetProperties().Select(p => p.Name));
        Assert.Equal(1, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Stale_version_is_a_conflict()
    {
        var (_, _, line) = _f.Stores.Equipment.AddHierarchy();

        await Assert.ThrowsAsync<ConflictException>(() => _handler.HandleAsync(
            new UpdateEquipmentCommand(line.Id, 99, "Renamed", null, true, false, false, true), CancellationToken.None));
        Assert.Empty(_f.Stores.Events.Appended);
    }

    [Fact]
    public async Task Missing_equipment_is_not_found() =>
        await Assert.ThrowsAsync<NotFoundException>(() => _handler.HandleAsync(
            new UpdateEquipmentCommand(Guid.CreateVersion7(), 1, "X", null, false, false, false, true), CancellationToken.None));
}
