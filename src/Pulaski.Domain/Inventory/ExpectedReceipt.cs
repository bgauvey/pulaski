namespace Pulaski.Domain.Inventory;

/// <summary>
/// Supply known to be on its way (D-058): a purchase or transfer that will arrive, for the shortage check to show beside stock.
/// Pulaski doesn't place, price or plan these; the ERP or a person records them, and receiving a lot against one closes it.
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
    string CreatedBy)
{
    public const string Open = "open";
    public const string Received = "received";
    public const string Cancelled = "cancelled";

    public bool IsOpen => Status == Open;

    /// <summary>New values for an open one.</summary>
    /// <exception cref="DomainRuleException">It is no longer open, or the quantity isn't positive.</exception>
    public ExpectedReceipt Change(decimal quantity, Guid uomId, DateTimeOffset expectedAt, string? reference, string? supplier, string? note)
    {
        EnsureOpen("changed");
        return quantity <= 0
            ? throw new DomainRuleException("The expected quantity must be greater than zero.")
            : this with { Quantity = quantity, UomId = uomId, ExpectedAt = expectedAt, Reference = reference, Supplier = supplier, Note = note };
    }

    /// <exception cref="DomainRuleException">It is no longer open.</exception>
    public ExpectedReceipt Cancel()
    {
        EnsureOpen("cancelled");
        return this with { Status = Cancelled };
    }

    /// <summary>Closed by receiving <paramref name="lotId"/> against it; whatever was received, nothing more is expected.</summary>
    /// <exception cref="DomainRuleException">It is no longer open.</exception>
    public ExpectedReceipt Receive(Guid lotId)
    {
        EnsureOpen("received against");
        return this with { Status = Received, ReceivedLotId = lotId };
    }

    private void EnsureOpen(string what)
    {
        if (!IsOpen)
        {
            throw new DomainRuleException($"{Reference ?? "This expected receipt"} is {Status}; it can't be {what}.");
        }
    }
}
