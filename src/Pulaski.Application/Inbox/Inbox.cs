using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Inbox;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Inbox;

/// <summary>The kinds of decision the inbox gathers (#123, D-112), the permission that decides each, and their order on the page.</summary>
public static class InboxKinds
{
    public const string PartialBatch = "partial_batch";
    public const string RateProposal = "rate_proposal";
    public const string ErpChange = "erp_change";
    public const string SpcAlert = "spc_alert";
    public const string Labor = "labor";
    public const string Maintenance = "maintenance";
    public const string Checklist = "checklist";
    public const string Release = "release";

    public static IReadOnlyList<(string Kind, string Permission)> All { get; } =
    [
        (PartialBatch, Permissions.PartialBatchDecide),
        (SpcAlert, Permissions.QualityRecord),
        (Labor, Permissions.LaborApprove),
        (ErpChange, Permissions.WorkOrderManage),
        (RateProposal, Permissions.RateModify),
        (Maintenance, Permissions.MaintenanceWork),
        (Checklist, Permissions.ChecklistSignOff),
        (Release, Permissions.ReleaseApprove),
    ];
}

/// <summary>What waits at sites (#123): read straight from the tables the deciding pages read.</summary>
public interface IInboxReadStore
{
    /// <summary>The active sites, with their codes.</summary>
    Task<IReadOnlyList<(Guid Id, string Code)>> ListSitesAsync(CancellationToken cancellationToken);

    /// <summary>The waiting items of <paramref name="kind"/> at these sites; labor comes from its own query.</summary>
    Task<IReadOnlyList<InboxItemDto>> ListAsync(string kind, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken);
}

/// <summary>Everything the user may decide, at every site where they hold the deciding permission (#123).</summary>
public sealed record GetInboxQuery(UserAccess Caller) : IQuery<InboxDto>;

internal sealed class GetInboxHandler(IInboxReadStore store, IQueryHandler<ListPendingLaborApprovalsQuery, PendingLaborApprovalsDto> labor)
    : IQueryHandler<GetInboxQuery, InboxDto>
{
    public async Task<InboxDto> HandleAsync(GetInboxQuery query, CancellationToken cancellationToken)
    {
        var sites = await store.ListSitesAsync(cancellationToken);
        var items = new List<InboxItemDto>();
        foreach (var (kind, permission) in InboxKinds.All)
        {
            var at = sites.Where(s => query.Caller.Can(permission, s.Id)).ToList();
            if (at.Count == 0)
            {
                continue;
            }

            if (kind != InboxKinds.Labor)
            {
                items.AddRange((await store.ListAsync(kind, [.. at.Select(s => s.Id)], cancellationToken)).OrderBy(i => i.Since));
                continue;
            }

            foreach (var site in at)
            {
                var pending = await labor.HandleAsync(new ListPendingLaborApprovalsQuery(site.Id), cancellationToken);
                items.AddRange(pending.Shifts.OrderBy(s => s.ProductionDate).ThenBy(s => s.Start).Select(s => LaborItem(site.Id, site.Code, s)));
            }
        }

        return new InboxDto { Items = items };
    }

    private static InboxItemDto LaborItem(Guid siteId, string siteCode, LaborShiftDto shift)
    {
        var day = shift.ProductionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new InboxItemDto
        {
            Kind = InboxKinds.Labor,
            Key = $"labor:{siteId:N}:{day}:{shift.ShiftScheduleId:N}:{shift.ShiftCode}",
            Title = $"{(shift.ShiftName ?? shift.ShiftCode ?? "Day")} of {shift.ProductionDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)}",
            Detail = shift.Sessions == 1 ? "1 session to approve" : $"{shift.Sessions} sessions to approve",
            SiteId = siteId,
            SiteCode = siteCode,
            Since = shift.End,
            Link = $"labor/review?day={day}",
        };
    }
}
