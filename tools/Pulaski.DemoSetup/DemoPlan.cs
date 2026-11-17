using System.Security.Cryptography;

namespace Pulaski.DemoSetup;

/// <summary>What the demo sets up: one edge node at the demo site, one Sparkplug B connection to the simulators' broker, and the tags of the
/// three simulated machines (deploy/simulator/compose.yaml).</summary>
public static class DemoPlan
{
    public const string EdgeNodeCode = "DEMO-EDGE";
    public const string ConnectionCode = "SIMULATORS";
    public const string EdgeUsername = "demo-edge";
    public const string GroupId = "Plant1";
    public const string ProfileCode = "PACKML-SIM";

    /// <summary>The simulators' machine profile (deploy/simulator/packml-simulator-profile.csv): they number their states their own way.</summary>
    public static string ProfileCsv { get; } = ReadProfile();

    /// <summary>The simulators' raw state value for a PackML state, from the profile; null if it has none.</summary>
    public static int? RawState(string csv, string packMlState) =>
        csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(','))
            .Where(f => f.Length >= 4 && f[0] == "state" && string.Equals(f[3], packMlState, StringComparison.OrdinalIgnoreCase))
            .Select(f => (int?)int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

    /// <summary>Each simulated machine: its Sparkplug node and device, and the Pulaski equipment it is.</summary>
    public static IReadOnlyList<Machine> Machines { get; } =
    [
        new("Extruder", "EXT-01-EXT"),
        new("Saw", "EXT-01-SAW"),
        new("Packer", "PACK1"),
    ];

    /// <summary>The metrics bound on every machine, by role (the PackML simulator's names).</summary>
    public static IReadOnlyList<(string Role, string Metric)> Metrics { get; } =
    [
        ("state", "Status/StateCurrent"),
    ];

    /// <summary>A strong random password that meets Pulaski's rules (at least 10 characters, two kinds).</summary>
    public static string NewPassword() => "Demo-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>The edge's settings, as shell exports its container sources before starting.</summary>
    public static string EdgeEnvironment(Uri apiUrl, Guid edgeNodeId, string username, string password) =>
        $"""
        # Written by the demo setup (tools/Pulaski.DemoSetup): the edge node's sign-in. For this demo only.
        export Edge__ApiUrl='{apiUrl}'
        export Edge__EdgeNodeId='{edgeNodeId}'
        export Edge__Username='{username}'
        export Edge__Password='{password}'

        """;

    private static string ReadProfile()
    {
        using var stream = typeof(DemoPlan).Assembly.GetManifestResourceStream("packml-simulator-profile.csv")
            ?? throw new InvalidOperationException("The simulators' profile isn't built into the demo setup.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public sealed record Machine(string Node, string Device)
    {
        public string Address(string metric) => $"{GroupId}/{Node}/{Device}:{metric}";
    }
}
