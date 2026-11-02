namespace Pulaski.Contracts.Inventory;

/// <summary>Stock of a lot set aside for a job (D-064).</summary>
public sealed record LotAllocationDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required Guid MaterialId { get; init; }
    public required Guid LotId { get; init; }

    /// <summary>The lot's number, or "Balance at …" for a balance.</summary>
    public required string LotLabel { get; init; }
    public string? LocationCode { get; init; }

    /// <summary>Still set aside, in the lot's base unit.</summary>
    public decimal Quantity { get; init; }

    /// <summary>As set aside, before any of it was used.</summary>
    public decimal AllocatedQty { get; init; }
    public required string UomCode { get; init; }

    /// <summary>The lot's material when it is an approved alternate for the component (#82), at <see cref="Ratio"/> of it per one of the component.</summary>
    public string? AlternateMaterialCode { get; init; }
    public decimal? Ratio { get; init; }

    /// <summary><c>chosen</c> by a person or <c>recommended</c>.</summary>
    public required string Basis { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }

    /// <summary>Why it is no longer good (the lot is on hold, expires first, or no longer holds it); null while it is. Refresh puts it right.</summary>
    public string? Problem { get; init; }
}

/// <summary>A lot suggested for a component, and why it comes where it does (D-064).</summary>
public sealed record LotRecommendationDto
{
    public required Guid LotId { get; init; }
    public required string LotLabel { get; init; }
    public string? LocationCode { get; init; }

    /// <summary>What to allocate, in <see cref="UomId"/>: the component's base unit, or an alternate's own (#82).</summary>
    public decimal Quantity { get; init; }
    public Guid UomId { get; init; }
    public string UomCode { get; init; } = "";

    /// <summary>The lot's material when it is an approved alternate (#82), at <see cref="Ratio"/> of it per one of the component.</summary>
    public string? AlternateMaterialCode { get; init; }
    public decimal? Ratio { get; init; }
    public decimal Free { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public required string Reason { get; init; }
}

/// <summary>A lot of the component that wasn't suggested, and why.</summary>
public sealed record LotExclusionDto
{
    public required Guid LotId { get; init; }
    public required string LotLabel { get; init; }
    public string? LocationCode { get; init; }
    public decimal Quantity { get; init; }
    public required string Reason { get; init; }
}

/// <summary>One component of a job: what it needs, what is set aside for it, and what to use next.</summary>
public sealed record ComponentAllocationDto
{
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }

    /// <summary>The material's base unit, which every quantity here is in.</summary>
    public required Guid UomId { get; init; }
    public required string UomCode { get; init; }

    /// <summary>Planned, in the base unit; null when the plan's unit doesn't convert.</summary>
    public decimal? NeedQty { get; init; }
    public decimal ConsumedQty { get; init; }
    public decimal AllocatedQty { get; init; }

    /// <summary>Still to allocate: need less consumed less allocated, at least zero.</summary>
    public decimal RemainingQty { get; init; }
    public string? Problem { get; init; }
    public IReadOnlyList<LotAllocationDto> Allocations { get; init; } = [];
    public IReadOnlyList<LotRecommendationDto> Recommendations { get; init; } = [];
    public IReadOnlyList<LotExclusionDto> Excluded { get; init; } = [];
}

/// <summary>A job's allocations and lot recommendations (D-064), worked out now.</summary>
public sealed record JobAllocationsDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required Guid SiteId { get; init; }
    public required string JobStatus { get; init; }

    /// <summary>When the job needs its components: its planned start, or now once it is due or running. Lots expiring by then are left out.</summary>
    public DateTimeOffset NeededBy { get; init; }

    /// <summary>False for a job that is completed, closed or cancelled.</summary>
    public bool CanAllocate { get; init; }
    public IReadOnlyList<ComponentAllocationDto> Components { get; init; } = [];
}

public sealed record AllocateLotRequest
{
    public Guid LotId { get; init; }
    public decimal Quantity { get; init; }
    public Guid UomId { get; init; }
    public string? Note { get; init; }
}

public sealed record AllocateRecommendedRequest
{
    /// <summary>One component; blank for all of them.</summary>
    public Guid? MaterialId { get; init; }
}

public sealed record ReleaseAllocationRequest
{
    public string? Note { get; init; }
}

/// <summary>What an allocation change did.</summary>
public sealed record AllocationChangeResponse
{
    public int Allocated { get; init; }
    public int Released { get; init; }
    public int Trimmed { get; init; }
    public int Jobs { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>How a site allocates (#82): keep allocations current, refuse consuming another job's allocation, and the lot preferences in order.</summary>
public sealed record AllocationSettingsDto
{
    public required Guid SiteId { get; init; }

    /// <summary>Refreshed in the background a short while after stock or the schedule changes.</summary>
    public bool KeepCurrent { get; init; }

    /// <summary>Consuming stock allocated to another job is refused, not only warned about.</summary>
    public bool HardReservation { get; init; }

    /// <summary>Among lots expiring the same day, in this order: <c>near_line</c>, <c>in_container</c>, <c>whole_lot</c>, <c>part_lots_first</c>.</summary>
    public IReadOnlyList<string> Preferences { get; init; } = [];

    /// <summary>When the last background refresh started; null when there hasn't been one.</summary>
    public DateTimeOffset? RefreshedAt { get; init; }

    /// <summary>Version of the settings; 0 while the site has none saved.</summary>
    public long Version { get; init; }
}

public sealed record SetAllocationSettingsRequest
{
    public long Version { get; init; }
    public bool KeepCurrent { get; init; }
    public bool HardReservation { get; init; }
    public IReadOnlyList<string> Preferences { get; init; } = [];
}

/// <summary>What to fetch from the stores for open jobs' allocations (#82), nearest location first; stock already at the job's line is left out.</summary>
public sealed record PickingListDto
{
    public required Guid SiteId { get; init; }
    public required string SiteCode { get; init; }
    public required string SiteTimeZone { get; init; }

    /// <summary>The job or line it was asked for, or null for every open job at the site.</summary>
    public string? Scope { get; init; }
    public IReadOnlyList<PickingLineDto> Lines { get; init; } = [];
}

/// <summary>One line of a picking list (#82): an active allocation to fetch, by where it is.</summary>
public sealed record PickingLineDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string LineCode { get; init; }
    public DateTimeOffset? NeededFrom { get; init; }

    /// <summary>The component it is for; <see cref="MaterialCode"/> is the lot's own, different when it is an approved alternate.</summary>
    public required string ComponentCode { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public required Guid LotId { get; init; }
    public required string LotLabel { get; init; }
    public string? LocationCode { get; init; }
    public string? ContainerCode { get; init; }
    public decimal Quantity { get; init; }
    public required string UomCode { get; init; }
}
