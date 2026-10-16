using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Tests.WorkOrders;

/// <summary>The form's check of a process for a material: the same rules creating the order applies (D-024).</summary>
public class ProcessOrderCheckHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly CheckProcessForOrderHandler _handler;
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _material = Guid.CreateVersion7();

    public ProcessOrderCheckHandlerTests() => _handler = new CheckProcessForOrderHandler(_f.Stores.Processes, _f.Stores.Boms, _f.Time);

    [Fact]
    public async Task A_process_whose_lines_all_run_the_material_has_no_problems()
    {
        var line = Line(eligible: true);
        var route = _f.Stores.Processes.AddReleased(_site, Ops((10, line.Id)));

        var check = await _handler.HandleAsync(new CheckProcessForOrderQuery(route.Process.Id, _material), CancellationToken.None);

        Assert.Empty(check.Problems);
        Assert.Equal(route.Version.Id, check.ProcessVersionId);
        Assert.Empty(check.DepartmentOperations);
    }

    [Fact]
    public async Task Each_line_without_a_capability_is_named()
    {
        var ext = Line(eligible: true);
        var pack = Line(eligible: false);
        var route = _f.Stores.Processes.AddReleased(_site, Ops((10, ext.Id), (30, pack.Id)));

        var check = await _handler.HandleAsync(new CheckProcessForOrderQuery(route.Process.Id, _material), CancellationToken.None);

        var problem = Assert.Single(check.Problems);
        Assert.Equal(("no_capability", 30, pack.Code), (problem.Code, problem.OperationNumber, problem.EquipmentCode));
        Assert.Contains("MAT", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_department_operation_is_listed_so_the_form_asks_for_a_due_date()
    {
        var line = Line(eligible: true);
        var department = _f.Stores.Scheduling.AddGroup(_site, line);
        var route = _f.Stores.Processes.AddReleased(_site, Ops((20, department.Id)));

        var check = await _handler.HandleAsync(new CheckProcessForOrderQuery(route.Process.Id, _material), CancellationToken.None);

        Assert.Empty(check.Problems);
        Assert.Equal([20], check.DepartmentOperations);
    }

    [Fact]
    public async Task A_process_that_does_not_make_the_material_says_so()
    {
        var route = _f.Stores.Processes.AddReleased(
            _site, Ops((10, Line(eligible: true).Id)), ProcessScope.Of(false, [], [Guid.CreateVersion7()]));

        var check = await _handler.HandleAsync(new CheckProcessForOrderQuery(route.Process.Id, _material), CancellationToken.None);

        Assert.Equal(["not_covered"], check.Problems.Select(p => p.Code));
    }

    private SchedulingLine Line(bool eligible)
    {
        var line = _f.Stores.Scheduling.AddLine(_site);
        if (eligible)
        {
            _f.Stores.Scheduling.AddCapability(line.Id, _material);
        }

        return line;
    }

    private static ProcessDefinition Ops(params (int Number, Guid Equipment)[] ops) =>
        new([.. ops.Select(o => new ProcessOperation(Guid.CreateVersion7(), o.Number, $"Op {o.Number}", null, 0, 0, [new OperationEquipment(o.Equipment, 100m)]))], []);
}
