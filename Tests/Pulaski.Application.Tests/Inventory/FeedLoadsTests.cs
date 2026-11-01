using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Lots loaded at feed points (#33), worked out from their movements, and how a time window shares among them.</summary>
public class FeedLoadsTests
{
    private static readonly DateTimeOffset T0 = HandlerFixture.Now;
    private static readonly Guid Hopper = Guid.CreateVersion7();
    private static readonly Guid Stores = Guid.CreateVersion7();
    private static readonly Guid Resin = Guid.CreateVersion7();
    private static readonly Guid Colour = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid C = Guid.CreateVersion7();

    private static FeedEvent Moved(Guid lot, double hours, Guid from, Guid to, Guid? material = null) =>
        new(lot, material ?? Resin, T0.AddHours(hours), T0.AddHours(hours), LotEventKind.Moved, null, 100m, from, to);

    private static FeedEvent Used(Guid lot, double hours, decimal qty, decimal after) =>
        new(lot, Resin, T0.AddHours(hours), T0.AddHours(hours), LotEventKind.Consumed, -qty, after, Hopper, null);

    private static FeedEvent Loaded(Guid lot, double hours) =>
        new(lot, Resin, T0.AddHours(hours), T0.AddHours(hours), LotEventKind.Loaded, null, 100m, null, Hopper);

    [Fact]
    public void A_lot_feeds_from_its_arrival_until_the_next_lot_of_its_material_leaving_or_running_out()
    {
        var loads = FeedLoads.From(Hopper,
        [
            Moved(A, 0, Stores, Hopper),
            Moved(C, 0.5, Stores, Hopper, Colour),   // another material: loaded alongside, ends nothing
            Moved(B, 2, Stores, Hopper),             // B ends A
            Used(B, 3, 100m, 0m),                    // B runs out
            Loaded(A, 4),                            // A, still there, feeds again
            Moved(A, 5, Hopper, Stores),             // and leaves
        ]);

        Assert.Equal(
            [(A, T0, T0.AddHours(2)), (C, T0.AddHours(0.5), null), (B, T0.AddHours(2), T0.AddHours(3)), (A, T0.AddHours(4), T0.AddHours(5))],
            loads.Select(l => (l.LotId, l.LoadedAt, l.EndedAt)));
    }

    [Fact]
    public void A_time_window_shares_by_how_long_each_lot_was_loaded_and_a_report_without_a_span_goes_to_the_lot_loaded_then()
    {
        var loads = FeedLoads.From(Hopper, [Moved(A, 0, Stores, Hopper), Moved(B, 3, Stores, Hopper)]);

        var shares = FeedLoads.ShareByTime(loads, Resin, T0.AddHours(2), T0.AddHours(6), 100m);
        Assert.Equal([(A, 25m), (B, 75m)], shares.Select(s => (s.LotId, s.Quantity)));

        Assert.Equal((B, 10m), FeedLoads.ShareByTime(loads, Resin, T0.AddHours(6), T0.AddHours(6), 10m).Select(s => (s.LotId, s.Quantity)).Single());
        Assert.Empty(FeedLoads.ShareByTime(loads, Colour, T0, T0.AddHours(6), 10m));
    }
}
