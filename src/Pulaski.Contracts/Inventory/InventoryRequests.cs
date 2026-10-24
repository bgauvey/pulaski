namespace Pulaski.Contracts.Inventory;

/// <summary>
/// Receives a new lot at a location (D-027). The lot number is required for a lot-tracked
/// material and left blank for one that isn't, which adds to the location's balance instead.
/// </summary>
public sealed record ReceiveLotRequest : IMayBackdate
{
    /// <summary>When it counts, to back-date it (D-057): at most 31 days back, with a comment; needs <c>inventory.backdate</c>. Null: now.</summary>
    public DateTimeOffset? EffectiveAt { get; init; }

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
public sealed record AdjustLotRequest : IMayBackdate
{
    /// <summary>When it counts, to back-date it (D-057): at most 31 days back, with a comment; needs <c>inventory.backdate</c>. Null: now.</summary>
    public DateTimeOffset? EffectiveAt { get; init; }

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
public sealed record ConsumeLotRequest : IMayBackdate
{
    /// <summary>When it counts, to back-date it (D-057): at most 31 days back, with a comment; needs <c>inventory.backdate</c>. Null: now.</summary>
    public DateTimeOffset? EffectiveAt { get; init; }

    public Guid JobId { get; init; }
    public decimal Quantity { get; init; }
    public Guid UomId { get; init; }
    public string? Comment { get; init; }

    /// <summary>The run it was used in; the job's open run when left out (D-035).</summary>
    public Guid? RunId { get; init; }
}

/// <summary>
/// Declares a job's output into a lot at a location. A blank lot number is generated from the
/// job number; an existing number adds to it only when the same job made it there.
/// </summary>
public sealed record ProduceLotRequest : IMayBackdate
{
    /// <summary>When it counts, to back-date it (D-057): at most 31 days back, with a comment; needs <c>inventory.backdate</c>. Null: now.</summary>
    public DateTimeOffset? EffectiveAt { get; init; }

    public Guid LocationId { get; init; }
    public decimal Quantity { get; init; }
    public Guid UomId { get; init; }
    public string? LotNumber { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Comment { get; init; }

    /// <summary>The run it was made in; the job's open run when left out (D-035).</summary>
    public Guid? RunId { get; init; }
}

/// <summary>Splits part of a lot into a new lot (D-030). A blank number becomes <c>&lt;lot&gt;-1</c>, <c>-2</c>…</summary>
public sealed record SplitLotRequest
{
    public long Version { get; init; }
    public decimal Quantity { get; init; }
    public Guid UomId { get; init; }
    public string? NewLotNumber { get; init; }

    /// <summary>Where the new lot goes; blank: where the lot is.</summary>
    public Guid? ToLocationId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Merges the source lots into the lot in the URL.</summary>
public sealed record MergeLotsRequest
{
    public long Version { get; init; }
    public IReadOnlyList<Guid> SourceLotIds { get; init; } = [];
    public string? Comment { get; init; }
}

/// <summary>Ships quantity of an available lot out of the site; a blank quantity ships it all.</summary>
public sealed record ShipLotRequest
{
    public long Version { get; init; }
    public decimal? Quantity { get; init; }
    public Guid? UomId { get; init; }

    /// <summary>The delivery or shipment number; required.</summary>
    public string Reference { get; init; } = "";
    public string? Comment { get; init; }
}

/// <summary>Corrects a lot's number, supplier lot and expiry; the comment says why.</summary>
public sealed record CorrectLotRequest
{
    public long Version { get; init; }

    /// <summary>Blank for a balance, which has no number (D-031).</summary>
    public string? LotNumber { get; init; }
    public string? SupplierLot { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Comment { get; init; } = "";
}

/// <summary>Reverses the lot's latest movement, for an adjustment reason.</summary>
public sealed record ReverseMovementRequest
{
    public long Version { get; init; }
    public Guid EventId { get; init; }
    public Guid ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>A material's shelf life in days; null clears it (D-032).</summary>
public sealed record SetShelfLifeRequest
{
    public long Version { get; init; }
    public int? ShelfLifeDays { get; init; }
}

public sealed record StartStockCountRequest
{
    public Guid LocationId { get; init; }
    public string? Note { get; init; }
}

/// <summary>Counted quantities, in each lot's base unit; null clears a line's count.</summary>
public sealed record RecordCountsRequest
{
    public long Version { get; init; }
    public IReadOnlyList<CountedLineRequest> Lines { get; init; } = [];
}

public sealed record CountedLineRequest(Guid LineId, decimal? CountedQty);

public sealed record PostStockCountRequest
{
    public long Version { get; init; }

    /// <summary>A reason of kind <c>inventory_adjustment</c>, normally Count correction.</summary>
    public Guid ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

public sealed record CancelStockCountRequest
{
    public long Version { get; init; }
}

public sealed record CreateReasonCodeRequest
{
    /// <summary>Blank: every site.</summary>
    public Guid? SiteId { get; init; }
    public string Kind { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public bool RequiresComment { get; init; }
    public int SortOrder { get; init; }

    /// <summary>Required for a utilization reason (D-050): <c>runtime</c>, <c>downtime</c> or <c>planned</c>.</summary>
    public string? UtilizationClass { get; init; }
}

public sealed record UpdateReasonCodeRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public bool RequiresComment { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>A utilization reason's class (D-050); left out, it stays.</summary>
    public string? UtilizationClass { get; init; }
}

/// <summary>A movement request that may be back-dated (D-057); the API requires <c>inventory.backdate</c> when it is.</summary>
public interface IMayBackdate
{
    DateTimeOffset? EffectiveAt { get; }
}
