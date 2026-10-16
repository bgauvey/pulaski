using Pulaski.Application.Scheduling;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

/// <summary>One site with white and grey profiles at 100 m/h, shared by the scheduling command tests.</summary>
public abstract class SchedulingHandlerTestBase
{
    protected static readonly DateTimeOffset Now = HandlerFixture.Now;

    private protected HandlerFixture F { get; } = new();

    protected Guid Site { get; } = Guid.CreateVersion7();

    protected Guid Metre { get; } = Guid.CreateVersion7();

    protected Guid White { get; } = Guid.CreateVersion7();

    protected Guid Grey { get; } = Guid.CreateVersion7();

    private protected CreateJobHandler Create() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time);

    /// <summary>
    /// A line that can run white and grey (unless <paramref name="eligible"/> is false), last ran
    /// white, and changes over to grey in 30 minutes.
    /// </summary>
    protected SchedulingLine Line(string mode = ChangeoverMode.Auto, bool eligible = true, Guid? site = null)
    {
        var line = F.Stores.Scheduling.AddLine(site ?? Site, changeoverMode: mode, plan: p => p with
        {
            PreviousMaterial = new PlanMaterialRow { MaterialId = White, MaterialCode = "WH" },
            Rates =
            [
                new RunRateRow { MaterialId = White, UomId = Metre, RatePerHour = 100m, RateSource = RateSource.Calculated },
                new RunRateRow { MaterialId = Grey, UomId = Metre, RatePerHour = 100m, RateSource = RateSource.Calculated },
            ],
            StandardChangeovers = [new StandardChangeoverRow { MaterialId = Grey, StandardChangeover = TimeSpan.FromMinutes(30) }],
        });

        if (eligible)
        {
            F.Stores.Scheduling.AddCapability(line.Id, White);
            F.Stores.Scheduling.AddCapability(line.Id, Grey);
        }

        return line;
    }

    protected async Task<WorkOrder> WorkOrderAsync(Guid material, string status = WorkOrderStatus.Planned, Guid? site = null, decimal qty = 1000m)
    {
        var planned = WorkOrder.CreateLocal(
            Guid.CreateVersion7(), site ?? Site, "WO-" + Guid.NewGuid().ToString("N")[..6], material, qty, Metre,
            null, null, null, null, 0);
        var wo = planned with { Status = status };
        await F.Stores.WorkOrders.InsertAsync(wo, Now, CancellationToken.None);
        return wo;
    }

    protected async Task<Guid> CreateProductionAsync(SchedulingLine line, WorkOrder wo, string number, Guid? before = null, decimal qty = 100m) =>
        (await Create().HandleAsync(
            new CreateJobCommand(line.Id, number, JobType.Production, wo.Id, qty, null, false, null, before),
            CancellationToken.None)).Id;

    /// <summary>Sets a stored work order's status directly, as if earlier commands had moved it there.</summary>
    protected Task SetStatusAsync(WorkOrder wo, string status) =>
        F.Stores.WorkOrders.InsertAsync(Current(wo) with { Status = status }, Now, CancellationToken.None);

    protected Job Job(Guid id) => F.Stores.Scheduling.FindJob(id)!.Value;

    protected long Version(Guid id) => F.Stores.Scheduling.FindJob(id)!.Version;

    protected long WorkOrderVersion(WorkOrder wo) => F.Stores.WorkOrders.Find(wo.Id)!.Version;

    protected WorkOrder Current(WorkOrder wo) => F.Stores.WorkOrders.Find(wo.Id)!.Value;
}
