using Pulaski.Contracts.Health;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Security;

/// <summary>How the system health page words what it shows (#141). Pure.</summary>
public static class SystemHealthView
{
    public static string Section(Text l, string key) => key switch
    {
        "database" => l["Database"],
        "services" => l["Background services"],
        "edge" => l["Edge nodes"],
        "deliveries" => l["Delivery feeds"],
        "connections" => l["Scheduled connections"],
        "rollups" => l["OEE rollups"],
        "mail" => l["E-mail"],
        _ => key,
    };

    /// <summary>Where a section's problems are put right, when it has one screen for all.</summary>
    public static string? SectionLink(string key) => key switch
    {
        "edge" => "edge-nodes",
        "deliveries" => "integrations",
        _ => null,
    };

    /// <summary>An item's name: services and database checks in words; edge nodes, feeds and connections by their codes.</summary>
    public static string Name(Text l, string section, string name) => (section, name) switch
    {
        ("database", "migration") => l["Schema version"],
        ("database", "partitions") => l["History partitions"],
        ("mail", "smtp") => l["Mail server"],
        ("services", "partitions") => l["Partition upkeep"],
        ("services", "deliveries") => l["Delivery relay"],
        ("services", "delivery_retention") => l["Delivery clean-up"],
        ("services", "oee_rollups") => l["OEE rollups"],
        ("services", "allocation_refresh") => l["Allocation refresh"],
        ("services", "rate_refresh") => l["Planning rate refresh"],
        ("services", "maintenance_plans") => l["Preventive maintenance"],
        ("services", "tag_sampling") => l["Process value sampling"],
        ("services", "connections") => l["Connection scheduler"],
        ("services", "qualification_notices") => l["Qualification expiry notices"],
        ("services", "alerts") => l["E-mail alerts"],
        ("services", "live") => l["Live updates"],
        _ => name,
    };

    public static string Status(Text l, string status) => status switch
    {
        "failing" => l["Failing"],
        "warning" => l["Needs a look"],
        "off" => l["Off"],
        _ => l["Healthy"],
    };

    public static string Chip(string status) => status switch
    {
        "failing" => "chip chip-danger",
        "warning" => "chip chip-warning",
        "off" => "chip",
        _ => "chip chip-live",
    };

    /// <summary>A line for the whole page: everything healthy, or how many items fail and need a look.</summary>
    public static string Summary(Text l, SystemHealthDto health)
    {
        var items = health.Sections.SelectMany(s => s.Items).ToList();
        int failing = items.Count(i => i.Status == "failing"), warning = items.Count(i => i.Status == "warning");
        return (failing, warning) switch
        {
            (0, 0) => l["Everything checked is healthy."],
            (0, 1) => l["1 item needs a look."],
            (0, _) => l["{0} items need a look.", warning],
            (1, 0) => l["1 item is failing."],
            (_, 0) => l["{0} items are failing.", failing],
            _ => l["{0} failing, {1} needing a look.", failing, warning],
        };
    }
}
