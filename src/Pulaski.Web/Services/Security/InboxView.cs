using Pulaski.Contracts.Inbox;
using Pulaski.Contracts.Security;

namespace Pulaski.Web.Services.Security;

/// <summary>How the inbox reads (#123). Pure but for the user's permissions.</summary>
public static class InboxView
{
    /// <summary>The permissions that decide what the inbox gathers; without any of them there is no inbox.</summary>
    public static IReadOnlyList<string> Deciding { get; } =
        [PermissionCodes.PartialBatchDecide, PermissionCodes.QualityRecord, PermissionCodes.LaborApprove, PermissionCodes.WorkOrderManage, PermissionCodes.RateModify];

    /// <summary>How often the menu recounts by itself, and the least time between recounts when the user moves around.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);

    public static bool MayDecideAny(CurrentUser user) => Deciding.Any(p => user.Can(p));

    public static string Badge(int count) => count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A kind's heading on the page: "Partial batches to approve", "SPC alerts to close"…</summary>
    public static string Heading(string kind) => kind switch
    {
        "partial_batch" => "Partial batches to approve or reject",
        "spc_alert" => "SPC alerts to close",
        "labor" => "Labor to approve",
        "erp_change" => "ERP changes to work orders",
        "rate_proposal" => "Planning rates to review",
        _ => kind,
    };

    /// <summary>The items grouped by kind, in the order the API gave them.</summary>
    public static IReadOnlyList<(string Kind, IReadOnlyList<InboxItemDto> Items)> Groups(IReadOnlyList<InboxItemDto> items) =>
        [.. items.GroupBy(i => i.Kind).Select(g => (g.Key, (IReadOnlyList<InboxItemDto>)[.. g]))];
}
