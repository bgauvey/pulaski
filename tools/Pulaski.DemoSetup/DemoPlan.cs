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

    /// <summary>
    /// Lines whose state is one machine's (D-050): EXT-01's comes from its extruder. Its puller has no simulator, so "worst of its machines"
    /// would read no signal; without this the line has no state of its own, no OEE, and counts as a line with no signal.
    /// </summary>
    public static IReadOnlyList<(string Line, string Constraint)> ConstraintMachines { get; } = [("EXT-01", "EXT-01-EXT")];

    /// <summary>The process-value simulator's Sparkplug node (tools/Pulaski.ProcessSimulator), publishing for the extruder.</summary>
    public const string ProcessNode = "ExtruderProcess";

    /// <summary>
    /// What is bound, by equipment and role: every machine's state, unit mode and speed; the saw's and packer's counts (the saw cuts
    /// EXT-01's output, so its line has one counter); and the extruder's process values from the process simulator.
    /// </summary>
    public static IReadOnlyList<Tag> Tags { get; } =
    [
        .. Machines.SelectMany(m => new[]
        {
            new Tag(m.Device, "state", m.Address("Status/StateCurrent")),
            new Tag(m.Device, "unit_mode", m.Address("Status/UnitModeCurrent")),
            new Tag(m.Device, "machine_speed", m.Address("Status/CurMachSpeed")),
        }),
        .. Machines.Where(m => m.Device != "EXT-01-EXT").SelectMany(m => new[]
        {
            new Tag(m.Device, "processed_count", m.Address("Admin/ProdProcessedCount/0/AccCount")),
            new Tag(m.Device, "defective_count", m.Address("Admin/ProdDefectiveCount/0/AccCount")),
        }),
        new Tag("EXT-01-EXT", "process_value", $"{GroupId}/{ProcessNode}/EXT-01-EXT:Process/MeltTemperature"),
        new Tag("EXT-01-EXT", "process_value", $"{GroupId}/{ProcessNode}/EXT-01-EXT:Process/ProfileWidth"),
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

    /// <summary>What the demo makes on the extrusion line, and the order it runs.</summary>
    public const string MaterialCode = "PRF-4410-WH";
    public const string LineCode = "EXT-01";
    public const string ProcessCode = "DEMO-EXTRUDE";
    public const string OrderNumber = "DEMO-0001";
    public const decimal OrderQuantity = 50_000;

    /// <summary>
    /// The extruder's two process values, charted (D-099): individuals and moving range, sampled every minute, against limits from the
    /// process simulator's design (tools/Pulaski.ProcessSimulator) and a specification wide enough for melt temperature to be capable and the
    /// profile's width marginal.
    /// </summary>
    public static IReadOnlyList<Characteristic> Characteristics { get; } =
    [
        new("MELT-TEMP", "Melt temperature (°C)", null, 1, $"{GroupId}/{ProcessNode}/EXT-01-EXT:Process/MeltTemperature", 215.0, 1.2, 209m, 221m),
        new("PROFILE-WIDTH", "Profile width", "mm", 2, $"{GroupId}/{ProcessNode}/EXT-01-EXT:Process/ProfileWidth", 42.00, 0.04, 41.85m, 42.15m),
    ];

    /// <summary>One charted process value: its characteristic, the tag it is sampled from, the simulator's design and the specification.</summary>
    public sealed record Characteristic(string Code, string Name, string? UomCode, int Decimals, string TagAddress, double Target, double Sigma, decimal Lsl, decimal Usl)
    {
        /// <summary>Individuals and moving-range limits from the design: centre at the target, ±3 sigma, the moving range's expected d2 × sigma.</summary>
        public IndividualsLimits Limits()
        {
            var movingRange = Pulaski.Domain.Quality.Spc.ChartConstants.RangeBias(2) * Sigma;
            var spread = Pulaski.Domain.Quality.Spc.ChartConstants.E2 * movingRange;
            return new(Target, Target + spread, Target - spread, movingRange, Pulaski.Domain.Quality.Spc.ChartConstants.MovingRangeD4 * movingRange);
        }
    }

    public sealed record IndividualsLimits(double Center, double Ucl, double Lcl, double MovingRange, double MovingRangeUcl);

    /// <summary>One tag to bind: the equipment, its role and the Sparkplug address.</summary>
    public sealed record Tag(string EquipmentCode, string Role, string Address);

    public sealed record Machine(string Node, string Device)
    {
        public string Address(string metric) => $"{GroupId}/{Node}/{Device}:{metric}";
    }
}
