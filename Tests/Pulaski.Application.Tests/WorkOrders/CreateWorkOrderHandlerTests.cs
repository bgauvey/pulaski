using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain;
using Pulaski.Domain.Processes;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.WorkOrders;

/// <summary>Work orders made from a process (D-024): the process assigns the equipment.</summary>
public class CreateWorkOrderHandlerTests
{
    private static readonly DateTimeOffset Start = HandlerFixture.Now.AddHours(2);

    private readonly HandlerFixture _f = new();
    private readonly CreateWorkOrderHandler _handler;
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _material;

    public CreateWorkOrderHandlerTests()
    {
        _handler = new CreateWorkOrderHandler(
            _f.Stores.UnitOfWork, _f.Stores.WorkOrders, _f.Stores.Processes, _f.Stores.Materials, _f.Stores.Scheduling,
            _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        _material = _f.Stores.Materials.AddActive();
    }

    [Fact]
    public async Task Each_operation_becomes_jobs_on_its_equipment_for_its_share()
    {
        var (ext1, ext2, pack) = (Line(), Line(), Line());
        var route = ExtrudeAndPack([(ext1.Id, 60m), (ext2.Id, 40m)], pack.Id);

        var result = await _handler.HandleAsync(Command(route.Process.Id, qty: 1000m, notes: "  Rush  "), CancellationToken.None);

        var order = _f.Stores.WorkOrders.Find(result.Id)!.Value;
        Assert.Equal((WorkOrderStatus.Planned, _site, route.Version.Id, "Rush"), (order.Status, order.SiteId, order.ProcessVersionId, order.Notes));
        Assert.Equal(Start, order.PlannedStart);

        var extrude = route.Version.Definition.Operations[0];
        Assert.Equal(("WO-1-10-1", 600m, extrude.Id), Single(ext1.Id));
        Assert.Equal(("WO-1-10-2", 400m, extrude.Id), Single(ext2.Id));
        Assert.Equal(("WO-1-30", 1000m, route.Version.Definition.Operations[1].Id), Single(pack.Id));
        Assert.All(_f.Stores.Scheduling.JobsOn(ext1.Id), j => Assert.Equal((JobStatus.Planned, Start), (j.Status, j.EarliestStart)));

        Assert.Equal(
            [EventTypes.WorkOrderCreated, EventTypes.JobCreated, EventTypes.JobCreated, EventTypes.JobCreated],
            _f.Stores.Events.Appended.Select(e => e.Type));
        Assert.Equal(3, _f.Stores.Scheduling.StatusChanges.Count);
        Assert.Equal(1, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task A_following_operation_is_held_until_the_link_lag_has_passed()
    {
        var (ext, pack) = (Line(), Line());
        var route = ExtrudeAndPack([(ext.Id, 100m)], pack.Id, lag: TimeSpan.FromMinutes(45));

        await _handler.HandleAsync(Command(route.Process.Id), CancellationToken.None);

        Assert.Equal(Start, Assert.Single(_f.Stores.Scheduling.JobsOn(ext.Id)).EarliestStart);
        Assert.Equal(Start.AddMinutes(45), Assert.Single(_f.Stores.Scheduling.JobsOn(pack.Id)).EarliestStart);
    }

    [Fact]
    public async Task Jobs_go_at_the_end_of_their_line()
    {
        var line = Line();
        var route = _f.Stores.Processes.AddReleased(_site, OneOperation(line.Id));

        await _handler.HandleAsync(Command(route.Process.Id, number: "WO-A"), CancellationToken.None);
        await _handler.HandleAsync(Command(route.Process.Id, number: "WO-B"), CancellationToken.None);

        Assert.Equal(["WO-A-10", "WO-B-10"], _f.Stores.Scheduling.JobsOn(line.Id).OrderBy(j => j.SequenceRank).Select(j => j.JobNumber));
    }

    [Fact]
    public async Task A_department_operation_plans_a_window_that_ends_at_the_due_date()
    {
        var line = Line();
        var department = _f.Stores.Scheduling.AddGroup(_site, line);
        var route = _f.Stores.Processes.AddReleased(_site, OneOperation(department.Id));

        var noDue = await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(Command(route.Process.Id), CancellationToken.None));
        Assert.Contains("due date", noDue.Message, StringComparison.Ordinal);

        var due = Start.AddDays(2);
        await _handler.HandleAsync(Command(route.Process.Id, due: due), CancellationToken.None);

        var job = Assert.Single(_f.Stores.Scheduling.JobsOn(department.Id));
        Assert.True(job.IsOnGroup);
        Assert.Equal((Start, due), (job.EarliestStart, job.LatestEnd));
    }

    [Fact]
    public async Task Every_operation_must_be_able_to_run_the_material()
    {
        var ext = Line();
        var pack = Line(eligible: false);
        var route = ExtrudeAndPack([(ext.Id, 100m)], pack.Id);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(Command(route.Process.Id), CancellationToken.None));

        Assert.Contains("Operation 30 (Pack)", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_f.Stores.Scheduling.JobsOn(ext.Id));
        Assert.Equal(0, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Equipment_in_another_site_is_refused()
    {
        var elsewhere = _f.Stores.Scheduling.AddLine(Guid.CreateVersion7());
        _f.Stores.Scheduling.AddCapability(elsewhere.Id, _material);
        var route = _f.Stores.Processes.AddReleased(_site, OneOperation(elsewhere.Id));

        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(Command(route.Process.Id), CancellationToken.None));
    }

    [Fact]
    public async Task The_process_must_make_the_material()
    {
        var route = _f.Stores.Processes.AddReleased(_site, OneOperation(Line().Id), ProcessScope.Of(false, [], [Guid.CreateVersion7()]));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(Command(route.Process.Id), CancellationToken.None));
        Assert.Contains("doesn't make", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_process_needs_a_released_version()
    {
        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(Command(Guid.CreateVersion7()), CancellationToken.None));
    }

    [Fact]
    public async Task The_material_must_be_active()
    {
        var route = _f.Stores.Processes.AddReleased(_site, OneOperation(Line().Id));

        await Assert.ThrowsAsync<DomainRuleException>(() => _handler.HandleAsync(
            Command(route.Process.Id) with { MaterialId = Guid.CreateVersion7() }, CancellationToken.None));
    }

    [Fact]
    public async Task Start_is_required_and_notes_are_limited()
    {
        var route = _f.Stores.Processes.AddReleased(_site, OneOperation(Line().Id));

        var invalid = await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(
            Command(route.Process.Id, notes: new string('x', CreateWorkOrderHandler.NotesMaxLength + 1)) with { PlannedStart = default },
            CancellationToken.None));

        Assert.Contains(nameof(CreateWorkOrderCommand.PlannedStart), invalid.Errors.Keys);
        Assert.Contains(nameof(CreateWorkOrderCommand.Notes), invalid.Errors.Keys);
    }

    private SchedulingLine Line(bool eligible = true)
    {
        var line = _f.Stores.Scheduling.AddLine(_site);
        if (eligible)
        {
            _f.Stores.Scheduling.AddCapability(line.Id, _material);
        }

        return line;
    }

    private (string JobNumber, decimal? Qty, Guid? Operation) Single(Guid lineId)
    {
        var job = Assert.Single(_f.Stores.Scheduling.JobsOn(lineId));
        return (job.JobNumber, job.PlannedQty, job.ProcessOperationId);
    }

    private ReleasedProcess ExtrudeAndPack(IReadOnlyList<(Guid Line, decimal Share)> extruders, Guid pack, TimeSpan? lag = null)
    {
        var extrude = new ProcessOperation(
            Guid.CreateVersion7(), 10, "Extrude", null, 0, 0, [.. extruders.Select(e => new OperationEquipment(e.Line, e.Share))]);
        var packing = new ProcessOperation(Guid.CreateVersion7(), 30, "Pack", null, 300, 0, [new OperationEquipment(pack, 100m)]);
        var link = new ProcessDependency(Guid.CreateVersion7(), extrude.Id, packing.Id, DependencyType.AfterStart, lag ?? TimeSpan.Zero);
        return _f.Stores.Processes.AddReleased(_site, new ProcessDefinition([extrude, packing], [link]));
    }

    private static ProcessDefinition OneOperation(Guid equipmentId) =>
        new([new ProcessOperation(Guid.CreateVersion7(), 10, "Make", null, 0, 0, [new OperationEquipment(equipmentId, 100m)])], []);

    private CreateWorkOrderCommand Command(
        Guid processId, string number = "WO-1", decimal qty = 500m, DateTimeOffset? due = null, string? notes = null) =>
        new(processId, number, _material, qty, Guid.CreateVersion7(), Start, due, 0, notes);
}
