namespace Pulaski.Web.Services.Help;

/// <summary>
/// Which section of the user guide each screen's help opens (#130): routes as the pages declare them (a <c>{…}</c> segment matches any
/// one), each with its section's anchor. A test checks every page has one and every anchor is in the guide. Pure.
/// </summary>
public static class HelpTopics
{
    public static IReadOnlyList<(string Route, string Anchor)> All { get; } =
    [
        ("", "the-overview-page"),
        ("help", "contents"),
        ("inbox", "your-inbox"),
        ("preferences", "your-language-and-region"),
        ("my-alerts", "get-e-mails-when-something-needs-you-my-alerts"),
        ("setup", "3-setting-up-what-to-do-first"),
        ("account/login", "sign-in"),
        ("account/password", "change-your-password"),
        ("account/forgot", "forgot-your-password"),
        ("account/reset", "forgot-your-password"),
        ("work-orders", "9-the-work-orders-screen"),
        ("schedule", "10-the-schedule"),
        ("schedule/work-orders/new", "8-creating-a-work-order"),
        ("floor", "11-the-floor-running-jobs-and-reporting-production"),
        ("floor/production", "production-by-shift"),
        ("jobs/{}", "the-job-page"),
        ("andon", "answer-calls-for-help-from-the-andon-page"),
        ("handover", "hand-over-to-the-next-shift"),
        ("machines", "21-machines-live-status-and-stops"),
        ("inventory", "find-what-you-have-the-stock-tab"),
        ("inventory/lots", "find-a-lot-the-lots-tab"),
        ("inventory/lots/{}", "a-lots-page"),
        ("inventory/materials/{}", "a-material-lots-locations-and-where-used"),
        ("inventory/jobs", "record-what-a-job-used-and-made-the-jobs-tab"),
        ("inventory/jobs/{}", "record-what-a-job-used-and-made-the-jobs-tab"),
        ("inventory/shortages", "check-for-shortages"),
        ("inventory/incoming", "incoming-deliveries"),
        ("inventory/journal", "the-movement-journal"),
        ("inventory/counts", "count-a-location"),
        ("inventory/counts/{}", "count-a-location"),
        ("inventory/reports", "reports-and-exports"),
        ("inventory/locations", "locations"),
        ("inventory/containers", "containers"),
        ("inventory/feed-points", "feed-points"),
        ("inventory/scan", "scan-labels"),
        ("quality/results", "record-a-result"),
        ("quality/specs", "write-a-specification"),
        ("quality/specs/{}", "write-a-specification"),
        ("quality/characteristics", "add-characteristics"),
        ("quality/characteristics/{}/control-limits", "set-control-limits"),
        ("quality/charts", "control-charts"),
        ("quality/alerts", "respond-to-spc-alerts"),
        ("quality/ncrs", "nonconformance-reports-ncrs"),
        ("quality/ncrs/{}", "nonconformance-reports-ncrs"),
        ("labor", "17-labor-people-and-crews"),
        ("labor/review", "review-and-approve-a-shift"),
        ("labor/reports", "labor-reports"),
        ("equipment", "4-equipment"),
        ("machine-profiles", "19-machine-profiles"),
        ("machine-profiles/{}", "19-machine-profiles"),
        ("edge-nodes", "20-edge-nodes-collecting-machine-data"),
        ("edge-nodes/{}", "20-edge-nodes-collecting-machine-data"),
        ("terminals", "set-up-a-terminal-managers"),
        ("materials", "find-a-material-the-materials-tab"),
        ("materials/{}", "the-material-page"),
        ("materials/classes", "classes"),
        ("materials/lot-attributes", "lot-attributes"),
        ("units", "6-units-and-conversions"),
        ("processes", "5-processes"),
        ("processes/{}", "the-editor"),
        ("boms", "7-bills-of-materials-boms"),
        ("boms/{}", "fill-it-in"),
        ("instructions", "work-instructions"),
        ("instructions/{}", "work-instructions"),
        ("changeovers", "changeover-rules"),
        ("rate-proposals", "review-proposed-planning-rates"),
        ("site-settings", "18-site-settings"),
        ("shifts", "16-shift-schedules"),
        ("reasons", "reason-codes"),
        ("people", "set-up-people-and-crews"),
        ("users", "15-users-roles-and-signing-in"),
        ("users/roles", "add-or-change-a-role-administrators"),
        ("users/permissions", "give-roles-permissions-one-at-a-time-administrators"),
        ("integrations", "send-movements-to-the-erp-and-certificates-to-a-document-system"),
        ("system-health", "system-health-administrators"),
        ("audit", "audit-trail-who-changed-what-administrators"),
        ("languages", "languages-and-translations-administrators"),
        ("terminal", "badge-at-a-terminal-everyone"),
        ("terminal/enrol", "set-up-a-terminal-managers"),
        ("terminal/station", "use-an-operator-station"),
        ("terminal/board", "show-a-line-board-on-a-tv"),
    ];

    /// <summary>Pages without help of their own: the error and not-found pages.</summary>
    public static IReadOnlySet<string> None { get; } = new HashSet<string>(StringComparer.Ordinal) { "Error", "not-found" };

    /// <summary>The section for <paramref name="path"/> (relative, as the browser has it, query and fragment ignored); null when none.</summary>
    public static string? For(string path)
    {
        var segments = Segments(path.Split('?', '#')[0]);
        return All.FirstOrDefault(t => Matches(Segments(t.Route), segments)).Anchor;
    }

    /// <summary>A route as a page declares it (<c>/jobs/{JobId:guid}</c>) in the form <see cref="All"/> uses (<c>jobs/{}</c>).</summary>
    public static string Normalize(string route) =>
        string.Join('/', Segments(route).Select(s => s.StartsWith('{') ? "{}" : s));

    private static string[] Segments(string path) => path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string[] route, string[] path) =>
        route.Length == path.Length && route.Zip(path).All(p => p.First == "{}" || string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
}
