using Pulaski.Contracts.Common;

namespace Pulaski.Contracts.Inventory;

/// <summary>
/// What a scan at a line can do in one step (#135): the scan as resolved, the line it was scanned at with its running job, and the actions that
/// fit, the likely one first. Each action carries what its command needs, so a confirm runs it as it was offered.
/// </summary>
public sealed record ScanActionsDto
{
    public required ScanResultDto Scan { get; init; }
    public ScanLineDto? Line { get; init; }
    public IReadOnlyList<ScanActionDto> Actions { get; init; } = [];
}

/// <summary>A line a device can be working at (#135): one that runs jobs.</summary>
public sealed record ScanLineOptionDto(Guid Id, string Code, string Name);

/// <summary>The line a scan was made at, and the job running there (its open run).</summary>
public sealed record ScanLineDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public Guid? RunningJobId { get; init; }
    public string? RunningJobNumber { get; init; }
    public Guid? RunId { get; init; }
}

/// <summary>
/// One action a scan offers (#135): <c>consume</c> a lot into the running job, <c>load</c> it at a feed point of the line, <c>move</c> it (or a
/// <c>move_container</c>) to the line, or <c>start</c> the job a shop order barcode names. <see cref="Permission"/> is what it needs at the site.
/// </summary>
public sealed record ScanActionDto
{
    public required string Kind { get; init; }
    public required string Permission { get; init; }

    public Guid? LotId { get; init; }
    public string? LotLabel { get; init; }
    public long? LotVersion { get; init; }
    public Guid? ContainerId { get; init; }
    public string? ContainerCode { get; init; }
    public long? ContainerVersion { get; init; }

    /// <summary>The job: consumed into, or started.</summary>
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }
    public long? JobVersion { get; init; }
    public Guid? RunId { get; init; }

    /// <summary>For consume: what the job still needs of the lot's material, capped at the lot (else the whole lot), in the lot's unit.</summary>
    public decimal? Quantity { get; init; }
    public decimal? Available { get; init; }
    public Guid? UomId { get; init; }
    public string? UomCode { get; init; }
    public string? MaterialCode { get; init; }

    /// <summary>Where a load or move goes, or the line a job starts on.</summary>
    public Guid? ToId { get; init; }
    public string? ToCode { get; init; }

    /// <summary>Something to know before confirming: off plan, beyond plan, already started elsewhere…</summary>
    public MessageDto? Warning { get; init; }
}
