using System.Globalization;

namespace Pulaski.Domain.Inventory;

/// <summary>Lot lifecycle (D-027). Values match <c>core.lot.status</c>.</summary>
public static class LotStatus
{
    public const string Available = "available";
    public const string OnHold = "on_hold";
    public const string Quarantine = "quarantine";
    public const string Consumed = "consumed";
    public const string Scrapped = "scrapped";
    public const string Shipped = "shipped";

    /// <summary>Its receipt was reversed (D-030).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Statuses whose quantity counts as on hand.</summary>
    public static IReadOnlyList<string> OnHand { get; } = [Available, OnHold, Quarantine];

    public static bool IsOnHand(string status) => OnHand.Contains(status);
}

/// <summary>What happened to a lot. Values match <c>ts.lot_event.event_kind</c>.</summary>
public static class LotEventKind
{
    public const string Received = "received";
    public const string Produced = "produced";
    public const string Adjusted = "adjusted";
    public const string Scrapped = "scrapped";
    public const string Consumed = "consumed";
    public const string Moved = "moved";
    public const string StatusChanged = "status_changed";
    public const string Split = "split";
    public const string Merged = "merged";
    public const string Shipped = "shipped";
    public const string Corrected = "corrected";
    public const string Reversed = "reversed";

    /// <summary>The movements a reversal can undo (D-030): one lot, and nothing a job declared.</summary>
    public static IReadOnlyList<string> Reversible { get; } = [Received, Adjusted, Scrapped, Moved, StatusChanged, Shipped];
}

/// <summary>Where a lot came from. Values match <c>core.lot.origin</c>.</summary>
public static class LotOrigin
{
    public const string Received = "received";
    public const string Produced = "produced";
    public const string Split = "split";
}

/// <summary>
/// One row of <c>ts.lot_event</c> as the lot sees it: the change and the quantity after it. The
/// application adds the job, reason, comment and actor.
/// </summary>
public sealed record LotMovement(
    string Kind,
    decimal? QtyDelta,
    decimal QtyAfter,
    Guid? FromLocationId = null,
    Guid? ToLocationId = null,
    string? FromStatus = null,
    string? ToStatus = null);

/// <summary>
/// A lot of one material at one location in a site (D-027). <see cref="Quantity"/> is in the
/// material's base unit and never goes below zero. A bulk lot is a balance: the one of a material
/// at its location, for a material that isn't lot-tracked or a location whose lots mix (D-031). It
/// has no lot number and stays available at zero, since it is the location's balance rather than a
/// lot that gets used up. Every change returns the new lot and the movement to record; the lot
/// itself is never changed in place.
/// </summary>
public sealed record Lot(
    Guid Id,
    Guid SiteId,
    Guid MaterialId,
    string? LotNumber,
    string Status,
    decimal Quantity,
    Guid UomId,
    Guid? LocationId,
    bool IsBulk,
    string Origin,
    string? SupplierLot = null,
    DateTimeOffset? ExpiresAt = null,
    Guid? ProducedByJobId = null)
{
    /// <summary>How messages name it: <c>Lot R-24-0917</c>, or <c>The balance</c>.</summary>
    public string Name => LotNumber is { } number ? $"Lot {number}" : "The balance";

    /// <summary>A generated number for a job's <paramref name="sequence"/>th lot: <c>WO-1001-10-03</c>.</summary>
    public static string ProducedNumber(string jobNumber, int sequence) =>
        $"{jobNumber}-{sequence.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>A new, empty lot at a location; <see cref="Add"/> then receives or produces into it.</summary>
    public static Lot Create(
        Guid id,
        Guid siteId,
        Guid materialId,
        string? lotNumber,
        Guid baseUomId,
        Guid locationId,
        bool isBulk,
        string origin,
        string? supplierLot = null,
        DateTimeOffset? expiresAt = null,
        Guid? producedByJobId = null) =>
        new(id, siteId, materialId, lotNumber, LotStatus.Available, 0m, baseUomId, locationId, isBulk, origin,
            supplierLot, expiresAt, producedByJobId);

    /// <summary>
    /// Adds stock: a receipt, production, or a write-up. A lot taken down to zero comes back as
    /// available.
    /// </summary>
    /// <exception cref="DomainRuleException">The quantity isn't positive, or the lot isn't one stock can be added to.</exception>
    public (Lot Lot, LotMovement Movement) Add(decimal quantity, string kind)
    {
        EnsurePositive(quantity);
        var status = Status switch
        {
            LotStatus.Consumed or LotStatus.Scrapped => LotStatus.Available,
            var s when LotStatus.IsOnHand(s) => s,
            _ => throw new DomainRuleException($"{Name} is {Label(Status)}; stock can't be added to it."),
        };

        var after = this with { Quantity = Quantity + quantity, Status = status };
        return (after, new LotMovement(kind, quantity, after.Quantity, ToLocationId: LocationId,
            FromStatus: status == Status ? null : Status, ToStatus: status == Status ? null : status));
    }

    /// <summary>
    /// Takes stock out by consumption or write-off. Only an available lot is consumed; a lot on
    /// hold or in quarantine can still be written off. At zero, a lot (not a bulk balance) becomes
    /// consumed or scrapped.
    /// </summary>
    /// <param name="quantity">In the base unit.</param>
    /// <param name="kind"><see cref="LotEventKind.Consumed"/>, <see cref="LotEventKind.Scrapped"/>, <see cref="LotEventKind.Shipped"/>,
    /// <see cref="LotEventKind.Adjusted"/> for a count, or <see cref="LotEventKind.Moved"/> for a bulk transfer.</param>
    /// <param name="unitCode">The base unit's code, for the message when there isn't enough.</param>
    /// <exception cref="DomainRuleException">Not enough stock, or the lot's status doesn't allow it.</exception>
    public (Lot Lot, LotMovement Movement) Take(decimal quantity, string kind, string unitCode)
    {
        EnsurePositive(quantity);
        if (kind is LotEventKind.Consumed or LotEventKind.Shipped && Status != LotStatus.Available)
        {
            var verb = kind == LotEventKind.Shipped ? "shipped" : "consumed";
            throw new DomainRuleException($"{Name} is {Label(Status)}; only an available lot can be {verb}. Release it first.");
        }

        EnsureOnHand(kind switch
        {
            LotEventKind.Consumed => "consumed",
            LotEventKind.Moved => "moved",
            LotEventKind.Shipped => "shipped",
            _ => "written off",
        });
        if (quantity > Quantity)
        {
            throw new DomainRuleException(
                $"{Name} has only {Qty(Quantity)} {unitCode}; {Qty(quantity)} {unitCode} can't be taken from it.");
        }

        var remaining = Quantity - quantity;
        var status = remaining == 0 && !IsBulk
            ? kind switch
            {
                LotEventKind.Scrapped => LotStatus.Scrapped,
                LotEventKind.Shipped => LotStatus.Shipped,
                _ => LotStatus.Consumed,
            }
            : Status;
        var after = this with { Quantity = remaining, Status = status };
        return (after, new LotMovement(kind, -quantity, remaining, FromLocationId: LocationId,
            FromStatus: status == Status ? null : Status, ToStatus: status == Status ? null : status));
    }

    /// <summary>Moves the whole lot to another location. A bulk balance is transferred by quantity instead.</summary>
    /// <exception cref="DomainRuleException">The lot is a bulk balance, isn't on hand, or is already there.</exception>
    public (Lot Lot, LotMovement Movement) MoveTo(Guid locationId)
    {
        if (IsBulk)
        {
            throw new DomainRuleException("A balance of a material that isn't lot-tracked moves by quantity; give the quantity to move.");
        }

        EnsureOnHand("moved");
        if (locationId == LocationId)
        {
            throw new DomainRuleException($"{Name} is already there.");
        }

        var after = this with { LocationId = locationId };
        return (after, new LotMovement(LotEventKind.Moved, null, Quantity, LocationId, locationId));
    }

    /// <summary>Takes <paramref name="quantity"/> out of a bulk balance to send to another location.</summary>
    /// <exception cref="DomainRuleException">The lot isn't a bulk balance, isn't on hand, or hasn't enough.</exception>
    public (Lot Lot, LotMovement Movement) TransferOut(decimal quantity, Guid toLocationId, string unitCode)
    {
        if (!IsBulk)
        {
            throw new DomainRuleException($"{Name} moves whole; splitting a lot isn't built yet.");
        }

        if (toLocationId == LocationId)
        {
            throw new DomainRuleException("The stock is already there.");
        }

        var (after, movement) = Take(quantity, LotEventKind.Moved, unitCode);
        return (after, movement with { ToLocationId = toLocationId });
    }

    /// <summary>Receives <paramref name="quantity"/> into a bulk balance from another location.</summary>
    public (Lot Lot, LotMovement Movement) TransferIn(decimal quantity, Guid fromLocationId)
    {
        var (after, movement) = Add(quantity, LotEventKind.Moved);
        return (after, movement with { FromLocationId = fromLocationId });
    }

    /// <summary>Holds, quarantines or releases the lot: a change between the on-hand statuses.</summary>
    /// <exception cref="DomainRuleException">The lot or the new status isn't on hand, or nothing changes.</exception>
    public (Lot Lot, LotMovement Movement) ChangeStatus(string status)
    {
        if (!LotStatus.IsOnHand(status))
        {
            throw new DomainRuleException($"A lot can be set to available, on hold or quarantine, not {Label(status)}.");
        }

        EnsureOnHand("held or released");
        if (status == Status)
        {
            throw new DomainRuleException($"{Name} is already {Label(status)}.");
        }

        var after = this with { Status = status };
        return (after, new LotMovement(LotEventKind.StatusChanged, null, Quantity, FromStatus: Status, ToStatus: status));
    }

    /// <summary>
    /// Splits <paramref name="quantity"/> off into a new lot of the same material, status, supplier
    /// lot and expiry at <paramref name="locationId"/> (D-030). Taking all of it is a move instead.
    /// </summary>
    /// <exception cref="DomainRuleException">A bulk balance, a lot not on hand, or a quantity that isn't less than the lot's.</exception>
    public (Lot Source, LotMovement Out, Lot Part, LotMovement In) Split(decimal quantity, Guid newId, string newNumber, Guid locationId, string unitCode)
    {
        EnsureWholeLot("split");
        EnsureOnHand("split");
        EnsurePositive(quantity);
        if (quantity >= Quantity)
        {
            throw new DomainRuleException(
                $"{Name} has {Qty(Quantity)} {unitCode}; split off less than that, or move the whole lot instead.");
        }

        var source = this with { Quantity = Quantity - quantity };
        var part = new Lot(newId, SiteId, MaterialId, newNumber, Status, quantity, UomId, locationId, false, LotOrigin.Split, SupplierLot, ExpiresAt);
        return (source, new LotMovement(LotEventKind.Split, -quantity, source.Quantity, LocationId, locationId),
            part, new LotMovement(LotEventKind.Split, quantity, quantity, LocationId, locationId));
    }

    /// <summary>
    /// Takes all of <paramref name="source"/> into this lot (D-030): the same material, at the same
    /// location, in the same status. The source is used up; this lot keeps the earlier expiry.
    /// </summary>
    /// <exception cref="DomainRuleException">The two lots can't be combined.</exception>
    public (Lot Target, LotMovement In, Lot Source, LotMovement Out) Absorb(Lot source)
    {
        EnsureWholeLot("merged");
        return Join(source);
    }

    /// <summary>
    /// Takes all of <paramref name="incoming"/>, a lot that has arrived at this balance's location,
    /// into the balance (D-031): at a location whose lots mix, every arrival ends here.
    /// </summary>
    /// <exception cref="DomainRuleException">This isn't a balance, or the lot can't join it.</exception>
    public (Lot Balance, LotMovement In, Lot Source, LotMovement Out) Commingle(Lot incoming)
    {
        if (!IsBulk)
        {
            throw new DomainRuleException("Only a balance takes in lots as they arrive.");
        }

        return Join(incoming);
    }

    private (Lot Target, LotMovement In, Lot Source, LotMovement Out) Join(Lot source)
    {
        source.EnsureWholeLot("merged");
        EnsureOnHand("merged into");
        if (source.Id == Id)
        {
            throw new DomainRuleException($"{Name} can't be merged into itself.");
        }

        if (source.MaterialId != MaterialId || source.SiteId != SiteId)
        {
            throw new DomainRuleException($"{source.Name} is another material or site; only lots of one material merge.");
        }

        if (source.LocationId != LocationId)
        {
            throw new DomainRuleException($"{source.Name} is at another location; move it there first.");
        }

        if (source.Status != Status)
        {
            throw new DomainRuleException($"{source.Name} is {Label(source.Status)} and the stock it joins is {Label(Status)}; lots merge only in the same status. Release or hold one of them first.");
        }

        if (source.Quantity == 0)
        {
            throw new DomainRuleException($"{source.Name} is empty.");
        }

        var expires = (ExpiresAt, source.ExpiresAt) switch
        {
            ({ } a, { } b) => a < b ? a : b,
            (var a, var b) => a ?? b,
        };
        var target = this with { Quantity = Quantity + source.Quantity, ExpiresAt = expires };
        var emptied = source with { Quantity = 0m, Status = LotStatus.Consumed };
        return (target, new LotMovement(LotEventKind.Merged, source.Quantity, target.Quantity, ToLocationId: LocationId),
            emptied, new LotMovement(LotEventKind.Merged, -source.Quantity, 0m, FromLocationId: LocationId, FromStatus: source.Status, ToStatus: LotStatus.Consumed));
    }

    /// <summary>Corrects the lot's number, supplier lot and expiry (D-030). A bulk balance's number can't change.</summary>
    /// <exception cref="DomainRuleException">Nothing changes, or a bulk balance's number would.</exception>
    public (Lot Lot, LotMovement Movement) Correct(string? lotNumber, string? supplierLot, DateTimeOffset? expiresAt)
    {
        if (IsBulk ? lotNumber is not null : string.IsNullOrWhiteSpace(lotNumber))
        {
            throw new DomainRuleException(IsBulk ? "A balance has no lot number." : "A lot needs a number.");
        }

        if (lotNumber == LotNumber && supplierLot == SupplierLot && expiresAt == ExpiresAt)
        {
            throw new DomainRuleException("Nothing to correct: the details are the same.");
        }

        var after = this with { LotNumber = lotNumber, SupplierLot = supplierLot, ExpiresAt = expiresAt };
        return (after, new LotMovement(LotEventKind.Corrected, null, Quantity));
    }

    /// <summary>
    /// Undoes <paramref name="original"/>, the lot's latest movement (D-030): the quantity, location
    /// and status go back to before it. A reversed receipt that leaves a lot at zero cancels it.
    /// </summary>
    /// <exception cref="DomainRuleException">The movement can't be reversed.</exception>
    public (Lot Lot, LotMovement Movement) Reverse(LotMovement original)
    {
        if (!LotEventKind.Reversible.Contains(original.Kind))
        {
            throw new DomainRuleException(original.Kind switch
            {
                LotEventKind.Reversed => "That movement is a reversal already.",
                LotEventKind.Consumed or LotEventKind.Produced => "Consumption and production are corrected with the job, not reversed here.",
                _ => $"A {Label(original.Kind)} movement can't be reversed; put it right with another movement.",
            });
        }

        var delta = original.QtyDelta ?? 0m;
        var quantity = Quantity - delta;
        if (quantity < 0)
        {
            throw new DomainRuleException($"{Name} no longer holds what that movement added.");
        }

        var location = original.Kind == LotEventKind.Moved ? original.FromLocationId ?? LocationId : LocationId;
        var status = original.ToStatus is not null ? original.FromStatus ?? Status : Status;
        if (original.Kind == LotEventKind.Received && quantity == 0 && !IsBulk)
        {
            status = LotStatus.Cancelled;
        }

        var after = this with { Quantity = quantity, LocationId = location, Status = status };
        return (after, new LotMovement(
            LotEventKind.Reversed,
            delta == 0 ? null : -delta,
            quantity,
            location == LocationId ? null : LocationId,
            location == LocationId ? null : location,
            status == Status ? null : Status,
            status == Status ? null : status));
    }

    private void EnsureWholeLot(string action)
    {
        if (IsBulk)
        {
            throw new DomainRuleException($"A balance of a material that isn't lot-tracked can't be {action}; it is already one quantity per location.");
        }
    }

    private void EnsureOnHand(string action)
    {
        if (!LotStatus.IsOnHand(Status))
        {
            throw new DomainRuleException($"{Name} is {Label(Status)}; it can't be {action}.");
        }
    }

    private static void EnsurePositive(decimal quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainRuleException("The quantity must be greater than zero.");
        }
    }

    private static string Label(string status) => status.Replace('_', ' ');

    private static string Qty(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
