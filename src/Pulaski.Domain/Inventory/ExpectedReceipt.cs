namespace Pulaski.Domain.Inventory;

/// <summary>
/// Supply known to be on its way (D-058): a purchase or transfer that will arrive, for the shortage check to show beside stock.
/// Pulaski doesn't place, price or plan these; the ERP or a person records them. Each lot received against one counts toward it
/// (<see cref="ReceivedQty"/>, in its unit, #83); it stays open for the rest until fully received or closed as complete.
/// </summary>
public sealed record ExpectedReceipt(
    Guid Id,
    Guid SiteId,
    Guid MaterialId,
    decimal Quantity,
    Guid UomId,
    DateTimeOffset ExpectedAt,
    string? Reference,
    string? Supplier,
    string? Note,
    string Status,
    Guid? ReceivedLotId,
    string? ExternalSystem,
    string? ExternalKey,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    decimal ReceivedQty = 0m)
{
    public const string Open = "open";
    public const string Received = "received";
    public const string Cancelled = "cancelled";

    public bool IsOpen => Status == Open;

    /// <summary>What is still expected, in its unit: nothing once it is closed.</summary>
    public decimal Remaining => IsOpen ? Math.Max(0m, Quantity - ReceivedQty) : 0m;

    /// <summary>New values for an open one.</summary>
    /// <exception cref="DomainRuleException">It is no longer open, or the quantity isn't positive.</exception>
    /// <exception cref="DomainRuleException">It is no longer open, the quantity isn't positive, or some has arrived and the unit or quantity would drop below it.</exception>
    public ExpectedReceipt Change(decimal quantity, Guid uomId, DateTimeOffset expectedAt, string? reference, string? supplier, string? note)
    {
        EnsureOpen("changed");
        if (quantity <= 0)
        {
            throw new DomainRuleException("The expected quantity must be greater than zero.");
        }

        if (ReceivedQty > 0 && (uomId != UomId || quantity <= ReceivedQty))
        {
            throw new DomainRuleException(uomId != UomId
                ? "Part of it has arrived, so its unit stays as it is."
                : $"{ReceivedQty:0.######} has arrived already; expect more than that, or close it as complete.");
        }

        return this with { Quantity = quantity, UomId = uomId, ExpectedAt = expectedAt, Reference = reference, Supplier = supplier, Note = note };
    }

    /// <exception cref="DomainRuleException">It is no longer open, or part of it has arrived (close it as complete instead).</exception>
    public ExpectedReceipt Cancel()
    {
        EnsureOpen("cancelled");
        return ReceivedQty > 0
            ? throw new DomainRuleException("Part of it has arrived; close it as complete instead.")
            : this with { Status = Cancelled };
    }

    /// <summary>
    /// <paramref name="quantity"/> (in its unit) arrived in <paramref name="lotId"/> (#83): it counts toward what is expected, and once all of it
    /// has arrived the receipt is received; until then it stays open for the rest.
    /// </summary>
    /// <exception cref="DomainRuleException">It is no longer open, or the quantity isn't positive.</exception>
    public ExpectedReceipt Receive(Guid lotId, decimal quantity)
    {
        EnsureOpen("received against");
        if (quantity <= 0)
        {
            throw new DomainRuleException("A receipt against it must be of more than zero.");
        }

        var arrived = ReceivedQty + quantity;
        return this with { ReceivedQty = arrived, ReceivedLotId = lotId, Status = arrived >= Quantity ? Received : Open };
    }

    /// <summary>Nothing more is coming (#83): closed with what has arrived.</summary>
    /// <exception cref="DomainRuleException">It is no longer open, or nothing has arrived (cancel it instead).</exception>
    public ExpectedReceipt Complete()
    {
        EnsureOpen("closed");
        return ReceivedQty <= 0
            ? throw new DomainRuleException("Nothing has arrived against it; cancel it instead.")
            : this with { Status = Received };
    }

    private void EnsureOpen(string what)
    {
        if (!IsOpen)
        {
            throw new DomainRuleException($"{Reference ?? "This expected receipt"} is {Status}; it can't be {what}.");
        }
    }
}
