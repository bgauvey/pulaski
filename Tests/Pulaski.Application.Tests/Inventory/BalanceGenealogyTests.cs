using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Genealogy through balances whose lots mix (D-060): which arrivals fed each withdrawal, and how sure that is.</summary>
public class BalanceGenealogyTests
{
    private static readonly DateTimeOffset T0 = HandlerFixture.Now;
    private static readonly Guid LotA = Guid.CreateVersion7();
    private static readonly Guid LotB = Guid.CreateVersion7();
    private static readonly Guid Job1 = Guid.CreateVersion7();
    private static readonly Guid Job2 = Guid.CreateVersion7();

    private static BalanceMovement In(Guid? lot, double hours, decimal qty) => new(Guid.CreateVersion7(), T0.AddHours(hours), qty, lot, null, "merged");

    private static BalanceMovement Out(Guid job, double hours, decimal qty) => new(Guid.CreateVersion7(), T0.AddHours(hours), -qty, null, job, "consumed");

    // 100 of A, then 100 of B, then 150 taken by job 1 and 50 by job 2.
    private static readonly BalanceMovement[] Silo = [In(LotA, 0, 100m), In(LotB, 1, 100m), Out(Job1, 2, 150m), Out(Job2, 3, 50m)];

    [Fact]
    public void First_in_first_out_takes_the_oldest_material_first()
    {
        var result = BalanceAllocation.Allocate(Silo, FlowModels.Fifo, null);

        Assert.Equal([(LotA, 100m), (LotB, 50m)], result[0].Sources.Select(s => (s.SourceLotId!.Value, s.Quantity)));
        Assert.Equal((LotB, 50m), (result[1].Sources.Single().SourceLotId!.Value, result[1].Sources.Single().Quantity));
        Assert.Equal(GenealogyPrecision.Estimated, result[0].Precision);

        // Only B was left for job 2: certain from the movements.
        Assert.Equal(GenealogyPrecision.Exact, result[1].Precision);
    }

    [Fact]
    public void Mixed_gives_every_arrival_still_there_its_share()
    {
        var result = BalanceAllocation.Allocate(Silo, FlowModels.Mixed, null);

        Assert.Equal([(LotA, 75m), (LotB, 75m)], result[0].Sources.Select(s => (s.SourceLotId!.Value, s.Quantity)));
        Assert.Equal([(LotA, 25m), (LotB, 25m)], result[1].Sources.Select(s => (s.SourceLotId!.Value, s.Quantity)));
        Assert.All(result, r => Assert.Equal(GenealogyPrecision.Estimated, r.Precision));
    }

    [Fact]
    public void Material_that_hasnt_stayed_long_enough_is_only_possible_and_quantities_that_dont_add_up_are_said_so()
    {
        // With a 2-hour residence, B (in at 1 h) can't leave at 2 h: job 1 gets A first, then B only possibly.
        var early = BalanceAllocation.Allocate(Silo, FlowModels.Fifo, TimeSpan.FromHours(2));
        Assert.Equal(GenealogyPrecision.Possible, early[0].Precision);
        Assert.Equal([GenealogyPrecision.Estimated, GenealogyPrecision.Possible], early[0].Sources.Select(s => s.Precision));
        Assert.Contains("minimum residence", early[0].Reason, StringComparison.Ordinal);

        var overdrawn = BalanceAllocation.Allocate([In(LotA, 0, 100m), Out(Job1, 1, 130m)], FlowModels.Fifo, null).Single();
        Assert.Equal(GenealogyPrecision.Possible, overdrawn.Precision);
        Assert.Equal((null, 30m), (overdrawn.Sources[^1].SourceLotId, overdrawn.Sources[^1].Quantity));
        Assert.Contains("isn't known", overdrawn.Reason, StringComparison.Ordinal);

        // Emptied, then refilled: the next withdrawal can only be the new arrival.
        var refilled = BalanceAllocation.Allocate([In(LotA, 0, 100m), Out(Job1, 1, 100m), In(LotB, 2, 80m), Out(Job2, 3, 30m)], FlowModels.Mixed, null);
        Assert.Equal((GenealogyPrecision.Exact, LotB), (refilled[1].Precision, refilled[1].Sources.Single().SourceLotId!.Value));
    }

    [Fact]
    public async Task A_trace_back_through_a_balance_keeps_only_the_arrivals_the_flow_model_gives_that_job()
    {
        var f = new HandlerFixture();
        var balance = Guid.CreateVersion7();
        var made = Guid.CreateVersion7();
        var supplierOfA = Guid.CreateVersion7();
        f.Stores.Inventory.BalanceHistories[balance] = new BalanceHistory(balance, FlowModels.Fifo, null, [.. Silo.Select(Row)]);
        f.Stores.Inventory.Traces[(made, false)] =
        [
            Node(made, null, 0, Job2),
            Node(balance, made, 1, null, isBulk: true),
            Node(LotA, balance, 2, null),
            Node(LotB, balance, 2, null),
            Node(supplierOfA, LotA, 3, null),
        ];

        var trace = await new TraceLotHandler(f.Stores.Inventory).HandleAsync(new TraceLotQuery(made, Forward: false), CancellationToken.None);

        // Job 2 took only B under first in, first out; A and what A came from are left out.
        Assert.Equal([made, balance, LotB], trace.Select(n => n.LotId));
        Assert.Equal(GenealogyPrecision.Exact, trace.Single(n => n.LotId == LotB).Precision);
        Assert.Contains("declared movements, not measured", trace.Single(n => n.LotId == LotB).PrecisionNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locations_flow_settings_are_changed_with_its_version()
    {
        var f = new HandlerFixture();
        var silo = Guid.CreateVersion7();
        f.Stores.Inventory.Flows[silo] = new(new FlowSettings(silo, Guid.CreateVersion7(), "SILO-1", true, FlowModels.Mixed, null), 3);
        var handler = new SetFlowHandler(f.Stores.UnitOfWork, f.Stores.Inventory, f.Stores.Events, f.Events);

        var set = await handler.HandleAsync(new SetFlowCommand(silo, 3, FlowModels.Fifo, 120), CancellationToken.None);

        Assert.Equal((FlowModels.Fifo, TimeSpan.FromHours(2)), (f.Stores.Inventory.Flows[silo].Value.FlowModel, f.Stores.Inventory.Flows[silo].Value.MinResidence!.Value));
        Assert.Equal(EventTypes.EquipmentFlowSet, f.Stores.Events.Appended[^1].Type);
        await Assert.ThrowsAsync<Errors.ConflictException>(() => handler.HandleAsync(new SetFlowCommand(silo, 3, FlowModels.Mixed, null), CancellationToken.None));
        await Assert.ThrowsAsync<Errors.ValidationException>(() => handler.HandleAsync(new SetFlowCommand(silo, set.Version, "lifo", null), CancellationToken.None));
    }

    private static BalanceMovementRow Row(BalanceMovement m) => new()
    {
        EventId = m.EventId,
        At = m.At,
        Delta = m.Delta,
        Kind = m.Kind,
        SourceLotId = m.SourceLotId,
        JobId = m.JobId,
    };

    private static LotTraceNodeDto Node(Guid lot, Guid? via, int depth, Guid? job, bool isBulk = false) => new()
    {
        LotId = lot,
        ViaLotId = via,
        Depth = depth,
        ProducedByJobId = job,
        IsBulk = isBulk,
        MaterialCode = "M",
        MaterialName = "M",
        Status = "available",
        UomCode = "kg",
    };
}
