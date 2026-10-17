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
}

/// <summary>Where a lot came from. Values match <c>core.lot.origin</c>.</summary>
public static class LotOrigin
{
    public const string Received = "received";
    public const string Produced = "produced";
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
/// material's base unit and never goes below zero. A bulk lot is the one balance of a material
/// that isn't lot-tracked at its location; it stays available at zero, since it is the location's
/// balance rather than a lot that gets used up. Every change returns the new lot and the movement
/// to record; the lot itself is never changed in place.
/// </summary>
public sealed record Lot(
    Guid Id,
    Guid SiteId,
    Guid MaterialId,
    string LotNumber,
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
    /// <summary>The lot number of a bulk balance: <c>@</c> and the location's code.</summary>
    public static string BulkNumber(string locationCode) => "@" + locationCode;

    /// <summary>A generated number for a job's <paramref name="sequence"/>th lot: <c>WO-1001-10-03</c>.</summary>
    public static string ProducedNumber(string jobNumber, int sequence) =>
        $"{jobNumber}-{sequence.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>A new, empty lot at a location; <see cref="Add"/> then receives or produces into it.</summary>
    public static Lot Create(
        Guid id,
        Guid siteId,
        Guid materialId,
        string lotNumber,
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
            _ => throw new DomainRuleException($"Lot {LotNumber} is {Label(Status)}; stock can't be added to it."),
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
    /// <param name="kind"><see cref="LotEventKind.Consumed"/>, <see cref="LotEventKind.Scrapped"/>, or <see cref="LotEventKind.Moved"/> for a bulk transfer.</param>
    /// <param name="unitCode">The base unit's code, for the message when there isn't enough.</param>
    /// <exception cref="DomainRuleException">Not enough stock, or the lot's status doesn't allow it.</exception>
    public (Lot Lot, LotMovement Movement) Take(decimal quantity, string kind, string unitCode)
    {
        EnsurePositive(quantity);
        if (kind == LotEventKind.Consumed && Status != LotStatus.Available)
        {
            throw new DomainRuleException($"Lot {LotNumber} is {Label(Status)}; only an available lot can be consumed. Release it first.");
        }

        EnsureOnHand(kind switch
        {
            LotEventKind.Consumed => "consumed",
            LotEventKind.Moved => "moved",
            _ => "written off",
        });
        if (quantity > Quantity)
        {
            throw new DomainRuleException(
                $"Lot {LotNumber} has only {Qty(Quantity)} {unitCode}; {Qty(quantity)} {unitCode} can't be taken from it.");
        }

        var remaining = Quantity - quantity;
        var status = remaining == 0 && !IsBulk
            ? (kind == LotEventKind.Scrapped ? LotStatus.Scrapped : LotStatus.Consumed)
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
            throw new DomainRuleException($"Lot {LotNumber} is already there.");
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
            throw new DomainRuleException($"Lot {LotNumber} moves whole; splitting a lot isn't built yet.");
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
            throw new DomainRuleException($"Lot {LotNumber} is already {Label(status)}.");
        }

        var after = this with { Status = status };
        return (after, new LotMovement(LotEventKind.StatusChanged, null, Quantity, FromStatus: Status, ToStatus: status));
    }

    private void EnsureOnHand(string action)
    {
        if (!LotStatus.IsOnHand(Status))
        {
            throw new DomainRuleException($"Lot {LotNumber} is {Label(Status)}; it can't be {action}.");
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
