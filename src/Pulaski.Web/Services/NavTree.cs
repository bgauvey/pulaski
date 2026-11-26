using Pulaski.Contracts.Security;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services;

/// <summary>A link in the menu: a screen, its icon, its text and tooltip (terms), and the views that show it.</summary>
public sealed record NavLinkItem(string Href, string Icon, string Text, string Title, string Permission, string? OrPermission = null);

/// <summary>
/// A menu entry (#199): a single link, or a group under a key with its main page (<see cref="Link"/>, null for a group that is only a folder,
/// named by <see cref="FolderText"/>) and the screens that belong with it.
/// </summary>
public sealed record NavEntry(NavLinkItem? Link, IReadOnlyList<NavLinkItem> Children, string? Key = null, string? FolderText = null, string? FolderIcon = null)
{
    public bool IsGroup => Key is not null;
}

/// <summary>A menu section: Operate, Configure.</summary>
public sealed record NavSection(string Label, IReadOnlyList<NavEntry> Entries);

/// <summary>
/// The sidebar menu (#199): sections of entries, like screens grouped under their main one. Only what the user may see is shown (D-039); a
/// group whose main page they can't see but some of whose screens they can becomes a folder of those. Pure.
/// </summary>
public static class NavTree
{
    public static IReadOnlyList<NavSection> Sections { get; } =
    [
        new(Term.Mark("Operate"),
        [
            One(new("", "overview", Term.Mark("Overview"), Term.Mark("Overview"), PermissionCodes.OverviewView)),
            Folder("planning", Term.Mark("Planning"), "schedule", [
                new("work-orders", "work-orders", Term.Mark("Work orders"), Term.Mark("Work orders"), PermissionCodes.WorkOrdersView),
                new("schedule", "schedule", Term.Mark("Schedule"), Term.Mark("Schedule"), PermissionCodes.ScheduleView),
                ]),
            Group("floor", new("floor", "labor", Term.Mark("Floor"), Term.Mark("Floor: run jobs and report production"), PermissionCodes.FloorView), [
                new("andon", "alert", Term.Mark("Andon"), Term.Mark("Andon: calls for help from the lines"), PermissionCodes.FloorView),
                new("handover", "kanban", Term.Mark("Handover"), Term.Mark("Shift handover: notes and open items per line"), PermissionCodes.FloorView),
                ]),
            One(new("machines", "gauge", Term.Mark("Machines"), Term.Mark("Machines: live state and stops to code"), PermissionCodes.MachinesView)),
            One(new("inventory", "lot", Term.Mark("Inventory"), Term.Mark("Inventory"), PermissionCodes.InventoryView)),
            One(new("quality/results", "spc", Term.Mark("Quality"), Term.Mark("Quality results: record and review what was measured or inspected"), PermissionCodes.QualityView)),
            One(new("labor", "users", Term.Mark("Labor"), Term.Mark("Labor: who is on, shift review and reports"), PermissionCodes.LaborView)),
        ]),
        new(Term.Mark("Configure"),
        [
            Group("equipment", new("equipment", "equipment", Term.Mark("Equipment"), Term.Mark("Equipment"), PermissionCodes.EquipmentView), [
                new("machine-profiles", "plug", Term.Mark("Machine profiles"), Term.Mark("Machine profiles: state values and stop codes"), PermissionCodes.EquipmentView),
                new("edge-nodes", "signal", Term.Mark("Edge nodes"), Term.Mark("Edge nodes: connections and tags that collect machine data"), PermissionCodes.EquipmentView),
                new("terminals", "plug", Term.Mark("Terminals"), Term.Mark("Shop-floor terminals for badging"), PermissionCodes.Terminals),
                ]),
            Group("materials", new("materials", "categorized", Term.Mark("Materials"), Term.Mark("Materials and classes"), PermissionCodes.MaterialsView), [
                new("units", "units", Term.Mark("Units"), Term.Mark("Units and conversions"), PermissionCodes.UnitsView),
                new("quality/specs", "spc", Term.Mark("Specifications"), Term.Mark("Material specifications and quality characteristics"), PermissionCodes.QualityView),
                ]),
            Group("processes", new("processes", "process", Term.Mark("Processes"), Term.Mark("Processes"), PermissionCodes.ProcessesView), [
                new("boms", "bom", Term.Mark("BOMs"), Term.Mark("Bills of materials"), PermissionCodes.ProcessesView),
                new("instructions", "info", Term.Mark("Work instructions"), Term.Mark("Work instructions shown at the job"), PermissionCodes.InstructionsView),
                new("changeovers", "refresh", Term.Mark("Changeovers"), Term.Mark("Changeover rules: how long each change of material takes"), PermissionCodes.ScheduleView, PermissionCodes.MaterialsView),
                new("rate-proposals", "gauge", Term.Mark("Planning rates"), Term.Mark("Planning rates from run history proposed for review"), PermissionCodes.MaterialsView, PermissionCodes.ScheduleView),
                ]),
            Group("site", new("site-settings", "calendar", Term.Mark("Site settings"), Term.Mark("Changeover mode, reporting tolerances, OEE targets, planning rates, holidays and shutdowns"), PermissionCodes.SiteSettingsView), [
                new("shifts", "clock", Term.Mark("Shifts"), Term.Mark("Shift schedules"), PermissionCodes.ShiftsView),
                new("reasons", "info", Term.Mark("Reasons"), Term.Mark("Reason codes"), PermissionCodes.ReasonsView),
                ]),
            Group("people", new("people", "user", Term.Mark("People"), Term.Mark("People and crews"), PermissionCodes.People), [
                new("users", "users", Term.Mark("Users"), Term.Mark("Users, roles and permissions"), PermissionCodes.Security),
                ]),
            Group("integrations", new("integrations", "signal", Term.Mark("Integrations"), Term.Mark("Integrations: events sent to the ERP and failed deliveries"), PermissionCodes.IntegrationsView), [
                new("system-health", "gauge", Term.Mark("System health"), Term.Mark("System health: what is unhealthy and where to put it right"), PermissionCodes.SystemView),
                new("languages", "categorized", Term.Mark("Languages"), Term.Mark("Languages and their translations"), PermissionCodes.Languages),
                ]),
        ]),
    ];

    /// <summary>
    /// The sections as <paramref name="may"/> lets the user see them: links they can't open are left out, a group they can open nothing of goes,
    /// one whose main page they can't open keeps its other screens as a folder, and a section left empty goes.
    /// </summary>
    public static IReadOnlyList<NavSection> Visible(Func<NavLinkItem, bool> may) =>
        [.. Sections
            .Select(s => s with { Entries = [.. s.Entries.Select(e => Visible(e, may)).OfType<NavEntry>()] })
            .Where(s => s.Entries.Count > 0)];

    private static NavEntry? Visible(NavEntry entry, Func<NavLinkItem, bool> may)
    {
        var link = entry.Link is { } l && may(l) ? l : null;
        var children = entry.Children.Where(may).ToList();
        if (!entry.IsGroup)
        {
            return link is null ? null : entry;
        }

        if (link is null && children.Count == 0)
        {
            return null;
        }

        return link is null && entry.FolderText is null
            ? entry with { Link = null, Children = children, FolderText = entry.Link!.Text, FolderIcon = entry.Link.Icon }
            : entry with { Link = link, Children = children };
    }

    /// <summary>Whether <paramref name="path"/> (relative, no leading slash) is the screen of <paramref name="href"/> or one below it.</summary>
    public static bool IsAt(string href, string path)
    {
        var p = path.Split('?', '#')[0].Trim('/');
        return href.Length == 0 ? p.Length == 0 : p == href || p.StartsWith(href + "/", StringComparison.Ordinal);
    }

    /// <summary>The group holding the screen at <paramref name="path"/>, which is always unfolded.</summary>
    public static string? GroupAt(IReadOnlyList<NavSection> sections, string path) =>
        sections.SelectMany(s => s.Entries).FirstOrDefault(e => e.IsGroup && (e.Link is { } l && IsAt(l.Href, path) || e.Children.Any(c => IsAt(c.Href, path))))?.Key;

    private static NavEntry One(NavLinkItem link) => new(link, []);

    private static NavEntry Group(string key, NavLinkItem main, IReadOnlyList<NavLinkItem> children) => new(main, children, key);

    private static NavEntry Folder(string key, string text, string icon, IReadOnlyList<NavLinkItem> children) => new(null, children, key, text, icon);
}

/// <summary>
/// Which menu groups are unfolded (#199, #205): those the reader opened (kept per browser) and the group of the screen they are on, unless they
/// folded it since arriving there. Every group folds and unfolds with its arrow, the current one too. Pure.
/// </summary>
public sealed class NavFolds
{
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folded = new(StringComparer.Ordinal);
    private string? _arrivedAt;

    /// <summary>The groups the reader opened, to keep in the browser.</summary>
    public IReadOnlyCollection<string> Opened => _open;

    public void Restore(IEnumerable<string> opened) => _open.UnionWith(opened);

    /// <summary>The screen's group is <paramref name="current"/>; arriving in another group unfolds it again.</summary>
    public void At(string? current)
    {
        if (current != _arrivedAt)
        {
            if (current is not null)
            {
                _folded.Remove(current);
            }

            _arrivedAt = current;
        }
    }

    public bool IsOpen(string key) => !_folded.Contains(key) && (_open.Contains(key) || key == _arrivedAt);

    /// <summary>Folds an unfolded group or unfolds a folded one; true when what is kept in the browser changed.</summary>
    public bool Toggle(string key)
    {
        if (IsOpen(key))
        {
            _folded.Add(key);
            return _open.Remove(key);
        }

        _folded.Remove(key);
        return _open.Add(key);
    }

    /// <summary>The group's main screen was opened: it unfolds and stays so; true when what is kept changed.</summary>
    public bool Opening(string key)
    {
        _folded.Remove(key);
        return _open.Add(key);
    }
}
