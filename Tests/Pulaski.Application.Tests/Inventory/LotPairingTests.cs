using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Per-lot genealogy (#81): pairing what a job used with what it made, and level readings on a balance's estimate.</summary>
public class LotPairingTests
{
    private static readonly DateTimeOffset T0 = HandlerFixture.Now;
    private static readonly Guid Job = Guid.CreateVersion7();
    private static readonly Guid Resin = Guid.CreateVersion7();
    private static readonly Guid Colour = Guid.CreateVersion7();
    private static readonly Guid R1 = Guid.CreateVersion7();
    private static readonly Guid R2 = Guid.CreateVersion7();
    private static readonly Guid R3 = Guid.CreateVersion7();
    private static readonly Guid C1 = Guid.CreateVersion7();
    private static readonly Guid P1 = Guid.CreateVersion7();
    private static readonly Guid P2 = Guid.CreateVersion7();
    private static readonly Guid P3 = Guid.CreateVersion7();

    private static JobDeclaration Used(Guid lot, Guid material, double hours) => new(Job, lot, material, false, T0.AddHours(hours), T0.AddHours(hours));

    private static JobDeclaration Made(Guid lot, double hours) => new(Job, lot, Guid.Empty, true, T0.AddHours(hours), T0.AddHours(hours));

    // R1 and the only colour lot at 0 h, P1 made at 1 h, R2 at 2 h, P2 at 3 h, P3 at 4 h, R3 used after everything at 5 h.
    private static readonly JobDeclaration[] Run =
        [Used(R1, Resin, 0), Used(C1, Colour, 0), Made(P1, 1), Used(R2, Resin, 2), Made(P2, 3), Made(P3, 4), Used(R3, Resin, 5)];

    [Fact]
    public void A_made_lot_pairs_with_the_lot_in_use_when_the_previous_output_was_declared_and_those_used_since()
    {
        var pairs = LotPairing.Pair(Run).ToDictionary(p => (p.ParentLotId, p.ChildLotId));

        Assert.Equal(GenealogyPrecision.Estimated, pairs[(R1, P1)].Precision);
        Assert.Equal(GenealogyPrecision.Exact, pairs[(C1, P1)].Precision);
        Assert.Equal(LotPairing.OnlyLotReason, pairs[(C1, P3)].Reason);

        // R1 was still running when P1 was declared, so P2 may have some of it; R2 alone ran through P3.
        Assert.Contains((R1, P2), pairs.Keys);
        Assert.Contains((R2, P2), pairs.Keys);
        Assert.Contains((R2, P3), pairs.Keys);
        Assert.DoesNotContain((R1, P3), pairs.Keys);
        Assert.DoesNotContain((R2, P1), pairs.Keys);

        // Used after everything was made: kept as possible, everywhere.
        Assert.All([P1, P2, P3], child => Assert.Equal((GenealogyPrecision.Possible, LotPairing.UsedAfterReason), (pairs[(R3, child)].Precision, pairs[(R3, child)].Reason)));
    }

    [Fact]
    public void An_output_declared_before_any_lot_of_a_material_keeps_every_lot_of_it_as_possible()
    {
        var pairs = LotPairing.Pair([Made(P1, 0), Used(R1, Resin, 1), Made(P2, 2)]).ToDictionary(p => (p.ParentLotId, p.ChildLotId));

        Assert.Equal((GenealogyPrecision.Possible, LotPairing.NothingBeforeReason), (pairs[(R1, P1)].Precision, pairs[(R1, P1)].Reason));
        Assert.Equal(GenealogyPrecision.Exact, pairs[(R1, P2)].Precision);
    }

    private static readonly Guid LotA = Guid.CreateVersion7();
    private static readonly Guid LotB = Guid.CreateVersion7();

    private static BalanceMovement In(Guid lot, double hours, decimal qty) => new(Guid.CreateVersion7(), T0.AddHours(hours), qty, lot, null, "merged");

    private static BalanceMovement Out(double hours, decimal qty) => new(Guid.CreateVersion7(), T0.AddHours(hours), -qty, null, Job, "consumed");

    [Fact]
    public void A_level_reading_empty_clears_what_the_declared_movements_left_and_confirms_the_next_arrival()
    {
        // 100 of A in, 60 taken: 40 still in by the declared movements; the level reads empty at 2 h, then B arrives and is taken.
        var take = Out(4, 30m);
        var movements = new[] { In(LotA, 0, 100m), Out(1, 60m), In(LotB, 3, 80m), take };
        var levels = new BalanceLevels([new EmptyPoint(T0.AddHours(2), T0.AddHours(1.5))], new Dictionary<Guid, decimal>(), null);

        var without = BalanceAllocation.Allocate(movements, FlowModels.Mixed, null);
        var with = BalanceAllocation.Allocate(movements, FlowModels.Mixed, null, levels);

        Assert.Equal(GenealogyPrecision.Estimated, without[1].Precision);
        Assert.Equal((GenealogyPrecision.Confirmed, LotB), (with[1].Precision, with[1].Sources.Single().SourceLotId!.Value));
        Assert.Contains("set aside", with[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_reading_before_the_level_caught_up_with_an_arrival_doesnt_clear_it()
    {
        // B arrives at 3 h but the level still reads empty at 3.1 h; the last reading above empty was at 1.5 h, before B.
        var movements = new[] { In(LotA, 0, 100m), Out(1, 60m), In(LotB, 3, 80m), Out(4, 30m) };
        var levels = new BalanceLevels([new EmptyPoint(T0.AddHours(3.1), T0.AddHours(1.5))], new Dictionary<Guid, decimal>(), null);

        var result = BalanceAllocation.Allocate(movements, FlowModels.Mixed, null, levels);

        Assert.Equal(LotB, result[1].Sources.Single().SourceLotId);
    }

    [Fact]
    public void A_level_that_agrees_with_declared_stock_confirms_an_exact_withdrawal_and_one_that_disagrees_makes_it_possible()
    {
        var agree = Out(1, 30m);
        var disagree = Out(2, 30m);
        var levels = new BalanceLevels([], new Dictionary<Guid, decimal> { [agree.EventId] = 99m, [disagree.EventId] = 20m }, Tolerance: 2m);

        var result = BalanceAllocation.Allocate([In(LotA, 0, 100m), agree, disagree], FlowModels.Mixed, null, levels);

        Assert.Equal(GenealogyPrecision.Confirmed, result[0].Precision);
        Assert.Equal(GenealogyPrecision.Possible, result[1].Precision);
        Assert.Contains("disagree", result[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trace_keeps_only_the_lots_a_made_lot_pairs_with_and_the_picked_ones_when_someone_named_them()
    {
        var f = new HandlerFixture();
        var inventory = f.Stores.Inventory;
        inventory.JobDeclarations.AddRange(Run);
        await inventory.InsertGenealogyAsync(
            [.. from parent in new[] { R1, R2, R3, C1 } from child in new[] { P1, P2, P3 } select new GenealogyEdge(Guid.CreateVersion7(), parent, child, Job)],
            T0, CancellationToken.None);
        inventory.Traces[(P3, false)] = [Node(P3, null, 0), Node(R1, P3, 1), Node(R2, P3, 1), Node(R3, P3, 1), Node(C1, P3, 1)];
        var handler = new TraceLotHandler(inventory, f.Stores.Units);

        var paired = await handler.HandleAsync(new TraceLotQuery(P3, Forward: false), CancellationToken.None);

        Assert.Equal([P3, R2, R3, C1], paired.Select(n => n.LotId));
        Assert.Equal(GenealogyPrecision.Estimated, paired.Single(n => n.LotId == R2).Precision);
        Assert.Equal(GenealogyPrecision.Possible, paired.Single(n => n.LotId == R3).Precision);

        // Someone picks R1 alone as what P3 was made from: only it is kept, as declared.
        await inventory.InsertGenealogyAsync([new GenealogyEdge(Guid.CreateVersion7(), R1, P3, Job, GenealogyEdge.MadeFrom)], T0, CancellationToken.None);

        var picked = await handler.HandleAsync(new TraceLotQuery(P3, Forward: false), CancellationToken.None);

        Assert.Equal([P3, R1], picked.Select(n => n.LotId));
        Assert.Equal((GenealogyPrecision.Declared, JobLinkPairs.PickedNote), (picked[1].Precision, picked[1].PrecisionNote));
    }

    private static LotTraceNodeDto Node(Guid id, Guid? via, int depth) => new()
    {
        LotId = id,
        ViaLotId = via,
        Depth = depth,
        ProducedByJobId = id == P1 || id == P2 || id == P3 ? Job : null,
        LotNumber = id.ToString()[..8],
        MaterialCode = "M",
        MaterialName = "M",
        Status = "available",
        UomCode = "kg",
    };
}
