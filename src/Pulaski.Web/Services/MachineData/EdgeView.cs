using System.Globalization;
using System.Text.Json;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>A connection setting as the screen edits it: free text, a tick box, or a choice.</summary>
public sealed record ConnectionSetting(string Key, string Label, string Placeholder, IReadOnlyList<(string Value, string Label)>? Choices = null, bool IsFlag = false);

/// <summary>Labels, settings and the configuration snippet for the edge node screens (D-050).</summary>
public static class EdgeView
{
    /// <summary>The protocols Pulaski Edge reads (Sparkplug B, OPC UA #22, EtherNet/IP #24), in the order the screen offers them.</summary>
    public static IReadOnlyList<(string Protocol, string Label)> Protocols { get; } =
    [
        ("sparkplug_b", "Sparkplug B (MQTT)"),
        ("opc_ua", "OPC UA"),
        ("ethernet_ip", "EtherNet/IP (Logix, Micro800, Omron NJ/NX)"),
    ];

    public static string ProtocolLabel(string protocol) => Protocols.FirstOrDefault(p => p.Protocol == protocol).Label ?? protocol;

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
        ("alarm", "Alarm (active number, 0 = none)"),
        ("warning", "Warning (active number, 0 = none)"),
        ("process_value", "Process value"),
        ("level", "Level (silo or tank)"),
    ];

    /// <summary>A process value's readings pass a deadband; nothing else has one.</summary>
    public static bool HasDeadband(string role) => role == "process_value";

    public static string RoleLabel(string role) => Roles.FirstOrDefault(r => r.Role == role).Label ?? role;

    public static bool IsCounter(string role) => role.EndsWith("_count", StringComparison.Ordinal);

    /// <summary>A protocol's settings, in the order the screen shows them. Passwords are never here: they go in the edge's configuration.</summary>
    public static IReadOnlyList<ConnectionSetting> Settings(string protocol) => protocol switch
    {
        "opc_ua" => OpcUaSettings,
        "ethernet_ip" => EthernetIpSettings,
        _ => SparkplugSettings,
    };

    public static IReadOnlyList<ConnectionSetting> SparkplugSettings { get; } =
    [
        new("host", "Broker host", "e.g. broker.plant1.local"),
        new("port", "Port", "1883, or 8883 with TLS"),
        new("tls", "TLS", "", IsFlag: true),
        new("groupId", "Group id", "all groups"),
        new("hostId", "Host id", "e.g. pulaski"),
        new("clientId", "Client id", "generated"),
        new("username", "User name", "none"),
    ];

    public static IReadOnlyList<ConnectionSetting> OpcUaSettings { get; } =
    [
        new("endpoint", "Endpoint", "opc.tcp://plc1:4840"),
        new("securityMode", "Security", "", [("", "Sign and encrypt"), ("sign", "Sign"), ("none", "None (test only)")]),
        new("securityPolicy", "Policy", "best offered"),
        new("serverThumbprint", "Server thumbprint", "from the trusted store"),
        new("trustAnyServer", "Trust any server (test only)", "", IsFlag: true),
        new("username", "User name", "anonymous"),
        new("publishingIntervalMs", "Publishing ms", "1000"),
        new("samplingIntervalMs", "Sampling ms", "as publishing"),
        new("browseRoot", "Browse from", "Objects (i=85)"),
        new("browseDepth", "Browse depth", "3"),
    ];

    public static IReadOnlyList<ConnectionSetting> EthernetIpSettings { get; } =
    [
        new("gateway", "PLC address", "e.g. 10.0.0.20"),
        new("path", "CIP path", "1,0 for a ControlLogix in slot 0"),
        new("plc", "PLC", "", [("", "ControlLogix"), ("compactlogix", "CompactLogix"), ("micro800", "Micro800"), ("omron-njnx", "Omron NJ/NX"),
            ("plc5", "PLC-5"), ("slc500", "SLC 500"), ("logixpccc", "Logix (PCCC)")]),
        new("pollIntervalMs", "Poll ms", "1000"),
        new("timeoutMs", "Timeout ms", "5000"),
    ];

    /// <summary>What an address of the protocol looks like, as a placeholder.</summary>
    public static string AddressPlaceholder(string? protocol) => protocol switch
    {
        "opc_ua" => "ns=2;s=Line1.State",
        "ethernet_ip" => "Program:Main.State|dint",
        _ => "group/node/device:metric",
    };

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
            .ToDictionary(c => c.Code, c => new Dictionary<string, string> { ["password"] = c.Protocol == "sparkplug_b" ? "<broker password>" : "<server password>" });
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
