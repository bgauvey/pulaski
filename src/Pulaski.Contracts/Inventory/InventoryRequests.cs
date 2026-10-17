namespace Pulaski.Contracts.Inventory;

/// <summary>
/// Receives a new lot at a location (D-027). The lot number is required for a lot-tracked
/// material and left blank for one that isn't, which adds to the location's balance instead.
/// </summary>
public sealed record ReceiveLotRequest
{
    public Guid MaterialId { get; init; }
    public Guid LocationId { get; init; }
    public string? LotNumber { get; init; }
    public decimal Quantity { get; init; }

    /// <summary>Any unit that converts to the material's base unit.</summary>
    public Guid UomId { get; init; }
    public string? SupplierLot { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Comment { get; init; }
}

/// <summary>A write-up (quantity required) or write-off (blank quantity takes the whole lot).</summary>
public sealed record AdjustLotRequest
{
    public long Version { get; init; }
    public decimal? Quantity { get; init; }
    public Guid? UomId { get; init; }

    /// <summary>A reason of kind <c>inventory_adjustment</c>.</summary>
    public Guid ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// Moves a lot to another location. A lot moves whole; a balance of a material that isn't
/// lot-tracked takes the quantity to move.
/// </summary>
public sealed record MoveLotRequest
{
    public long Version { get; init; }
    public Guid ToLocationId { get; init; }
    public decimal? Quantity { get; init; }
    public Guid? UomId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Holds, quarantines (reason of kind <c>hold</c> required) or releases a lot.</summary>
public sealed record ChangeLotStatusRequest
{
    public long Version { get; init; }

    /// <summary><c>available</c>, <c>on_hold</c> or <c>quarantine</c>.</summary>
    public string Status { get; init; } = "";
    public Guid? ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Declares quantity of an available lot used by a production job.</summary>
public sealed record ConsumeLotRequest
{
    public Guid JobId { get; init; }
    public decimal Quantity { get; init; }
    public Guid UomId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// Declares a job's output into a lot at a location. A blank lot number is generated from the
/// job number; an existing number adds to it only when the same job made it there.
/// </summary>
public sealed record ProduceLotRequest
{
    public Guid LocationId { get; init; }
    public decimal Quantity { get; init; }
    public Guid UomId { get; init; }
    public string? LotNumber { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Comment { get; init; }
}
