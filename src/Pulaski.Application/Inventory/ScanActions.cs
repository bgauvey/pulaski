using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Localization;
using Pulaski.Contracts.Common;
using Pulaski.Application.Units;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Inventory;

/// <summary>A line as a scan there sees it (#135): where things go, and what runs on it.</summary>
public sealed record ScanLine(
    Guid Id, string Code, Guid SiteId, bool HoldsInventory, Guid? JobId, string? JobNumber, Guid? RunId, IReadOnlyList<(Guid Id, string Code)> FeedPoints);

/// <summary>A lot as a scan sees it, in its own (the material's base) unit.</summary>
public sealed record ScanLot(
    Guid Id, string Label, Guid MaterialId, string MaterialCode, string Status, decimal Quantity, Guid UomId, string UomCode, Guid? LocationId, bool IsBulk, long Version);

public sealed record ScanContainer(Guid Id, string Code, Guid LocationId, long Version);

/// <summary>A job's planned use of one component, and what it has consumed so far in the material's base unit.</summary>
public sealed record ScanComponent(Guid MaterialId, decimal? PlannedQty, Guid? PlannedUomId, decimal ConsumedBaseQty);

/// <summary>A job a shop order barcode names, and whether it can be started on the line scanned at.</summary>
public sealed record ScanJob(Guid Id, string Number, string Status, long Version, Guid EquipmentId, bool OnLine, bool OnGroupAbove);

/// <summary>Reads for scanning at a line (#135).</summary>
public interface IScanContextReadStore
{
    Task<ScanLine?> GetLineAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>The site's active lines that run jobs.</summary>
    Task<IReadOnlyList<ScanLineOptionDto>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<ScanLot?> GetLotAsync(Guid lotId, CancellationToken cancellationToken);

    Task<ScanContainer?> GetContainerAsync(Guid containerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ScanComponent>> GetComponentsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The jobs of order <paramref name="orderNumber"/> at the site for operation <paramref name="operationNumber"/> (null: no operation).</summary>
    Task<IReadOnlyList<ScanJob>> FindJobsAsync(Guid siteId, string orderNumber, int? operationNumber, Guid lineId, CancellationToken cancellationToken);
}

/// <summary>What a scan of <paramref name="Code"/> at line <paramref name="LineId"/> can do in one step (#135); without a line, only the scan.</summary>
public sealed record GetScanActionsQuery(Guid SiteId, Guid? LineId, string Code) : IQuery<ScanActionsDto>;

/// <summary>The lines a device at the site can be working at (#135).</summary>
public sealed record ListScanLinesQuery(Guid SiteId) : IQuery<IReadOnlyList<ScanLineOptionDto>>;

internal sealed class ListScanLinesHandler(IScanContextReadStore store) : IQueryHandler<ListScanLinesQuery, IReadOnlyList<ScanLineOptionDto>>
{
    public Task<IReadOnlyList<ScanLineOptionDto>> HandleAsync(ListScanLinesQuery query, CancellationToken cancellationToken) =>
        store.ListLinesAsync(query.SiteId, cancellationToken);
}

internal sealed class GetScanActionsHandler(
    IQueryHandler<ResolveScanQuery, ScanResultDto> resolve, IScanContextReadStore store, IUnitReadStore units) : IQueryHandler<GetScanActionsQuery, ScanActionsDto>
{
    public async Task<ScanActionsDto> HandleAsync(GetScanActionsQuery query, CancellationToken cancellationToken)
    {
        var line = query.LineId is { } lineId ? await store.GetLineAsync(lineId, cancellationToken) : null;
        if (query.LineId is not null && (line is null || line.SiteId != query.SiteId))
        {
            throw new NotFoundException($"That line isn't at the site.");
        }

        var scan = await resolve.HandleAsync(new ResolveScanQuery(query.SiteId, query.Code), cancellationToken);
        if (line is null)
        {
            return new ScanActionsDto { Scan = scan };
        }

        var actions = new List<ScanActionDto>();
        if (scan.Status == "found" && scan.Matches is [var match])
        {
            if (match.Kind == "lot" && await store.GetLotAsync(match.Id, cancellationToken) is { } lot)
            {
                var components = line.JobId is { } job ? await store.GetComponentsAsync(job, cancellationToken) : [];
                var factor = await FactorAsync(lot, components, cancellationToken);
                actions.AddRange(ScanSuggestions.ForLot(line, lot, components, factor));
            }
            else if (match.Kind == "container" && await store.GetContainerAsync(match.Id, cancellationToken) is { } container)
            {
                actions.AddRange(ScanSuggestions.ForContainer(line, container));
            }
        }
        else if (ShopOrderCode.Parse(query.Code) is { } order)
        {
            var jobs = await store.FindJobsAsync(query.SiteId, order.OrderNumber, order.OperationNumber, line.Id, cancellationToken);
            actions.AddRange(ScanSuggestions.ForJobs(line, jobs));
        }

        return new ScanActionsDto
        {
            Scan = scan,
            Line = new ScanLineDto { Id = line.Id, Code = line.Code, RunningJobId = line.JobId, RunningJobNumber = line.JobNumber, RunId = line.RunId },
            Actions = actions,
        };
    }

    // The factor from the planned component's unit to the lot's, when the job plans the lot's material.
    private async Task<decimal?> FactorAsync(ScanLot lot, IReadOnlyList<ScanComponent> components, CancellationToken cancellationToken)
    {
        if (components.FirstOrDefault(c => c.MaterialId == lot.MaterialId) is not { PlannedUomId: { } from })
        {
            return null;
        }

        var converter = UnitConversions.For(lot.MaterialId, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([lot.MaterialId], cancellationToken));
        return converter.Factor(from, lot.UomId);
    }
}

/// <summary>A shop order's barcode (<c>ORDER|OP-10</c>, or <c>ORDER|OP-NA</c> for a job without an operation). Pure.</summary>
public sealed record ShopOrderCode(string OrderNumber, int? OperationNumber)
{
    public static ShopOrderCode? Parse(string? code)
    {
        var parts = (code ?? "").Trim().Split('|');
        if (parts.Length != 2 || parts[0].Length == 0 || !parts[1].StartsWith("OP-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var op = parts[1][3..];
        return op.Equals("NA", StringComparison.OrdinalIgnoreCase) ? new(parts[0], null)
            : int.TryParse(op, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? new(parts[0], n)
            : null;
    }
}

/// <summary>Which actions a scan at a line offers, the likely one first (#135). Pure.</summary>
public static class ScanSuggestions
{
    private static readonly string[] OnHand = ["available", "on_hold", "quarantine"];

    /// <summary>
    /// A lot: consume into the running job when it is available (what the job still needs, capped at the lot, else the whole lot; off plan or
    /// beyond plan warned, never refused), then load it at each feed point of the line or move it to the line, unless it is there already.
    /// </summary>
    public static IReadOnlyList<ScanActionDto> ForLot(ScanLine line, ScanLot lot, IReadOnlyList<ScanComponent> components, decimal? plannedToLot)
    {
        var actions = new List<ScanActionDto>();
        if (!OnHand.Contains(lot.Status) || lot.Quantity <= 0)
        {
            return actions;
        }

        if (line.JobId is { } jobId && lot.Status == "available")
        {
            var component = components.FirstOrDefault(c => c.MaterialId == lot.MaterialId);
            decimal quantity = lot.Quantity;
            MessageDto? warning = null;
            if (component is null)
            {
                warning = Notes.Of($"{line.JobNumber} doesn't plan to use {lot.MaterialCode}.");
            }
            else if (component.PlannedQty is { } planned && plannedToLot is { } factor)
            {
                var remaining = planned * factor - component.ConsumedBaseQty;
                if (remaining <= 0)
                {
                    warning = Notes.Of($"{line.JobNumber} has already used what it planned of {lot.MaterialCode}.");
                }
                else
                {
                    quantity = Math.Min(remaining, lot.Quantity);
                }
            }

            actions.Add(Lot(lot) with
            {
                Kind = "consume",
                Permission = Permissions.ProductionReport,
                JobId = jobId,
                JobNumber = line.JobNumber,
                RunId = line.RunId,
                Quantity = quantity,
                Available = lot.Quantity,
                UomId = lot.UomId,
                UomCode = lot.UomCode,
                Warning = warning,
            });
        }

        if (!lot.IsBulk)
        {
            foreach (var feed in line.FeedPoints.Where(f => f.Id != lot.LocationId))
            {
                actions.Add(Lot(lot) with { Kind = "load", Permission = Permissions.LotMove, ToId = feed.Id, ToCode = feed.Code });
            }
        }

        if (line.FeedPoints.Count == 0 && line.HoldsInventory && lot.LocationId != line.Id)
        {
            actions.Add(Lot(lot) with { Kind = "move", Permission = Permissions.LotMove, ToId = line.Id, ToCode = line.Code });
        }

        return actions;
    }

    /// <summary>A container: moved to the line, or to each of its feed points, unless it is there already.</summary>
    public static IReadOnlyList<ScanActionDto> ForContainer(ScanLine line, ScanContainer container)
    {
        var targets = line.HoldsInventory ? [(line.Id, line.Code), .. line.FeedPoints] : line.FeedPoints;
        return [.. targets.Where(t => t.Id != container.LocationId).Select(t => new ScanActionDto
        {
            Kind = "move_container",
            Permission = Permissions.LotMove,
            ContainerId = container.Id,
            ContainerCode = container.Code,
            ContainerVersion = container.Version,
            ToId = t.Id,
            ToCode = t.Code,
        })];
    }

    /// <summary>
    /// A shop order's job: start it on the line when it is planned there or to a group above it and may be started (dispatched, firmed, paused);
    /// another job running on the line is warned, and the start itself refuses what it must.
    /// </summary>
    public static IReadOnlyList<ScanActionDto> ForJobs(ScanLine line, IReadOnlyList<ScanJob> jobs) =>
        [.. jobs.Where(j => (j.OnLine || j.OnGroupAbove) && j.Status is JobStatus.Dispatched or JobStatus.Firmed or JobStatus.Paused && j.Id != line.JobId)
            .Select(j => new ScanActionDto
            {
                Kind = "start",
                Permission = Permissions.JobExecute,
                JobId = j.Id,
                JobNumber = j.Number,
                JobVersion = j.Version,
                ToId = line.Id,
                ToCode = line.Code,
                Warning = line.JobNumber is { } running ? Notes.Of($"{running} is running on {line.Code}: stop it first.") : null,
            })];

    private static ScanActionDto Lot(ScanLot lot) => new()
    {
        Kind = "",
        Permission = "",
        LotId = lot.Id,
        LotLabel = lot.Label,
        LotVersion = lot.Version,
        MaterialCode = lot.MaterialCode,
    };
}
