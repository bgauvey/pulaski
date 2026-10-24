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
    public decimal Quantity { get; init; }
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
