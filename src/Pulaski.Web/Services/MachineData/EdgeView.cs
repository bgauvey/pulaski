using System.Globalization;
using System.Text.Json;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>Labels, settings and the configuration snippet for the edge node screens (D-041).</summary>
public static class EdgeView
{
    /// <summary>The roles a tag can take today, with what each carries.</summary>
    public static IReadOnlyList<(string Role, string Label)> Roles { get; } =
    [
        ("state", "State (PackML StateCurrent)"),
        ("unit_mode", "Unit mode"),
        ("stop_reason", "Stop reason (first out)"),
        ("processed_count", "Processed count"),
        ("defective_count", "Defective count"),
        ("consumed_count", "Consumed count"),
        ("machine_speed", "Machine speed"),
    ];

    public static string RoleLabel(string role) => Roles.FirstOrDefault(r => r.Role == role).Label ?? role;

    public static bool IsCounter(string role) => role.EndsWith("_count", StringComparison.Ordinal);

    /// <summary>The Sparkplug B settings, in the order the screen shows them.</summary>
    public static IReadOnlyList<(string Key, string Label, string Placeholder)> SparkplugSettings { get; } =
    [
        ("host", "Broker host", "e.g. broker.plant1.local"),
        ("port", "Port", "1883, or 8883 with TLS"),
        ("tls", "TLS", ""),
        ("groupId", "Group id", "all groups"),
        ("hostId", "Host id", "e.g. pulaski"),
        ("clientId", "Client id", "generated"),
        ("username", "User name", "none"),
    ];

    /// <summary>When a node was last heard from: <c>just now</c>, <c>3 min ago</c>, or the site time; <c>never</c>.</summary>
    public static string LastHeard(DateTimeOffset? at, DateTimeOffset now, string? timeZone)
    {
        if (at is not { } seen)
        {
            return "never";
        }

        var ago = now - seen;
        return ago < TimeSpan.FromMinutes(1) ? "just now"
            : ago < TimeSpan.FromHours(1) ? $"{(int)ago.TotalMinutes} min ago"
            : Display.SiteTime(seen, timeZone);
    }

    /// <summary>A node that sent nothing for 2 minutes is quiet: offline, or nothing changed. The screen flags it.</summary>
    public static bool IsQuiet(DateTimeOffset? at, DateTimeOffset now) => at is not { } seen || now - seen > TimeSpan.FromMinutes(2);

    /// <summary>
    /// The <c>Edge</c> section of the edge's <c>appsettings.json</c> for <paramref name="node"/>: its id, the API it talks to, and a
    /// password slot for each connection with a user name. Secrets are placeholders, filled in on the edge's machine.
    /// </summary>
    public static string ConfigSnippet(EdgeNodeDetailDto node, Uri apiUrl)
    {
        var connections = node.Connections
            .Where(c => c.IsActive && c.Settings.ContainsKey("username"))
            .ToDictionary(c => c.Code, _ => new Dictionary<string, string> { ["password"] = "<broker password>" });
        var edge = new Dictionary<string, object>
        {
            ["ApiUrl"] = apiUrl.ToString(),
            ["EdgeNodeId"] = node.Node.Id.ToString(),
            ["Username"] = "<the edge's Pulaski user>",
            ["Password"] = "<its password>",
            ["BufferPath"] = "pulaski-edge.db",
            ["Connections"] = connections,
        };
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["Edge"] = edge }, Indented);
    }

    public static string Number(decimal value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>A speed or other reading for the screen: at most two decimals.</summary>
    public static string Reading(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // Shown to be copied into a file, not put into HTML: '<' and quotes stay as they are (Blazor encodes the text itself).
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
