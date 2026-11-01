using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Domain;

/// <summary>A lot's state at a past time (#77, D-076), worked back from now in the order movements count.</summary>
public class LotPastStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Dock = Guid.CreateVersion7();
    private static readonly Guid Rack = Guid.CreateVersion7();
    private static readonly Guid Pallet = Guid.CreateVersion7();

    private static PastEvent E(double hours, string kind, decimal? delta = null, Guid? from = null, Guid? to = null, string? fromStatus = null, string? toStatus = null,
        Guid? container = null, double? recordedHours = null) =>
        new(Guid.CreateVersion7(), T0.AddHours(hours), T0.AddHours(recordedHours ?? hours), kind, delta, from, to, fromStatus, toStatus, container);

    // Received 100 at the dock, packed on a pallet, moved to the rack, held, 30 consumed; a write-off of 10 entered late, counting from hour 2.
    private static readonly PastEvent[] Events =
    [
        E(0, LotEventKind.Received, 100m, to: Dock),
        E(1, LotEventKind.Packed, container: Pallet),
        E(3, LotEventKind.Moved, from: Dock, to: Rack),
        E(4, LotEventKind.StatusChanged, fromStatus: LotStatus.Available, toStatus: LotStatus.OnHold),
        E(5, LotEventKind.StatusChanged, fromStatus: LotStatus.OnHold, toStatus: LotStatus.Available),
        E(6, LotEventKind.Consumed, -30m, from: Rack),
        E(2, LotEventKind.Scrapped, -10m, from: Dock, recordedHours: 7),
    ];

    private static readonly LotNow Now = new(60m, Rack, LotStatus.Available, IsBulk: false);

    [Fact]
    public void Each_time_gives_the_quantity_place_status_and_container_the_journal_says()
    {
        var before = LotPastState.At(Now, Events, T0.AddHours(-1));
        Assert.Equal((false, 0m, (Guid?)null), (before.Existed, before.Quantity, before.ContainerId));
        Assert.Equal(new LotStateAt(true, 100m, Dock, LotStatus.Available, Pallet), LotPastState.At(Now, Events, T0.AddHours(1.5)));
        Assert.Equal(new LotStateAt(true, 90m, Dock, LotStatus.Available, Pallet), LotPastState.At(Now, Events, T0.AddHours(2.5)));
        Assert.Equal(new LotStateAt(true, 90m, Rack, LotStatus.OnHold, Pallet), LotPastState.At(Now, Events, T0.AddHours(4.5)));
        Assert.Equal(new LotStateAt(true, 60m, Rack, LotStatus.Available, Pallet), LotPastState.At(Now, Events, T0.AddHours(8)));
    }

    [Fact]
    public void A_quantity_row_names_places_but_never_moves_the_lot()
    {
        // A balance sends 40 to another: the row names both places, and the balance stays where it is.
        var transfer = new[] { E(0, LotEventKind.Received, 100m, to: Dock), E(1, LotEventKind.Moved, -40m, from: Dock, to: Rack) };
        var state = LotPastState.At(new LotNow(60m, Dock, LotStatus.Available, IsBulk: true), transfer, T0.AddHours(0.5));
        Assert.Equal((100m, Dock), (state.Quantity, state.LocationId));
    }

    [Fact]
    public void What_counts_after_a_time_is_found_and_a_change_of_status_or_place_is_told_apart()
    {
        Assert.Equal(LotEventKind.Moved, LotPastState.CountsAfter(Events, T0.AddHours(2.5))!.Kind);
        Assert.Equal(LotEventKind.StatusChanged, LotPastState.ChangedAfter(Events, T0.AddHours(3.5))!.Kind);
        Assert.Null(LotPastState.ChangedAfter(Events, T0.AddHours(5.5)));
        Assert.Equal(LotEventKind.Consumed, LotPastState.CountsAfter(Events, T0.AddHours(5.5))!.Kind);
        Assert.Null(LotPastState.CountsAfter(Events, T0.AddHours(6)));
    }
}
