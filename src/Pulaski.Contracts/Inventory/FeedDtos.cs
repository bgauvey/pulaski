namespace Pulaski.Contracts.Inventory;

/// <summary>A feed point (#33): where lots are loaded onto a line, with what is loaded there now.</summary>
public sealed record FeedPointDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary>The line it feeds: its nearest parent that runs jobs; null when none does.</summary>
    public Guid? LineId { get; init; }
    public string? LineCode { get; init; }

    /// <summary>It has an active consumed counter, so what it feeds is consumed by the count rather than by time.</summary>
    public bool HasCounter { get; init; }

    /// <summary>The lots loaded there now, latest first.</summary>
    public IReadOnlyList<FeedLoadDto> Loads { get; init; } = [];
}

/// <summary>A lot loaded at a feed point since a time.</summary>
public sealed record FeedLoadDto
{
    public required Guid LotId { get; init; }
    public string? LotNumber { get; init; }
    public bool IsBulk { get; init; }
    public string? LocationCode { get; init; }
    public required string MaterialCode { get; init; }
    public decimal Quantity { get; init; }
    public required string UomCode { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset LoadedAt { get; init; }
}

/// <summary>What an automated consumption couldn't cover (#33), until someone resolves or dismisses it.</summary>
public sealed record FeedShortfallDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }
    public Guid? MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    public Guid? LotId { get; init; }
    public string? LotNumber { get; init; }
    public decimal Quantity { get; init; }
    public string? UomCode { get; init; }

    /// <summary><c>backflush</c>, <c>time_window</c> or <c>counter</c>.</summary>
    public required string Source { get; init; }
    public Guid SourceRef { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string Reason { get; init; }

    /// <summary><c>open</c>, <c>resolved</c> or <c>dismissed</c>.</summary>
    public required string Status { get; init; }
    public Guid? ResolvedLotId { get; init; }
    public string? ResolvedLotNumber { get; init; }
    public string? ResolvedBy { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResolutionNote { get; init; }
    public long Version { get; init; }
}

/// <summary>Loads a lot at a feed point (#33): moves it there first if it is elsewhere.</summary>
public sealed record LoadLotRequest
{
    public long Version { get; init; }
    public Guid FeedPointId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Resolves a shortfall by consuming its quantity from a lot, into its job.</summary>
public sealed record ResolveShortfallRequest
{
    public long Version { get; init; }
    public Guid LotId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Dismisses a shortfall, saying why.</summary>
public sealed record DismissShortfallRequest
{
    public long Version { get; init; }
    public string Comment { get; init; } = "";
}
