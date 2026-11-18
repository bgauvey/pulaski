using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Security;
using Pulaski.DemoSetup;

// The demo's one-shot setup (D-101). Through the API, as the demo administrator, it adds an edge node at the demo site with a Sparkplug B
// connection to the simulators' broker and the simulated machines' tags, and a user for the edge; it writes the edge's sign-in to a file
// the edge container reads, then starts the simulated machines.
//
// The administrator's password is temporary, which allows nothing but changing it. So the setup changes it to a random one, does its work,
// then gives it back the demo's temporary password: the first person to sign in still chooses their own. If the administrator no longer
// has that password, someone has signed in already and the setup leaves the plant alone.
//
//   PULASKI_API_URL          the API, as this container reaches it (default http://api:8080/)
//   PULASKI_EDGE_API_URL     the API, as the edge reaches it (default: the same)
//   PULASKI_ADMIN_USERNAME   default admin
//   PULASKI_ADMIN_PASSWORD   the demo's temporary password (required)
//   PULASKI_SITE             the site's code (default PLT1)
//   PULASKI_MQTT_HOST/PORT   the simulators' broker (default broker:1883)
//   PULASKI_EDGE_FILE        where the edge's sign-in goes (default /shared/edge.env)
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = stop.Token;
var apiUrl = new Uri(Setting("PULASKI_API_URL", "http://api:8080/"));
var edgeApiUrl = new Uri(Setting("PULASKI_EDGE_API_URL", apiUrl.ToString()));
var adminUsername = Setting("PULASKI_ADMIN_USERNAME", "admin");
var adminPassword = Environment.GetEnvironmentVariable("PULASKI_ADMIN_PASSWORD") is { Length: > 0 } p
    ? p
    : throw new InvalidOperationException("Set PULASKI_ADMIN_PASSWORD to the demo administrator's temporary password.");
var siteCode = Setting("PULASKI_SITE", "PLT1");
var mqttHost = Setting("PULASKI_MQTT_HOST", "broker");
var mqttPort = int.Parse(Setting("PULASKI_MQTT_PORT", "1883"), System.Globalization.CultureInfo.InvariantCulture);
var edgeFile = Setting("PULASKI_EDGE_FILE", "/shared/edge.env");

if (File.Exists(edgeFile))
{
    Console.WriteLine($"The edge is set up already ({edgeFile}); starting the simulated machines.");
    await StartMachinesAsync(ct);
    return 0;
}

using var api = new PulaskiApi(new HttpClient { BaseAddress = apiUrl });
Console.WriteLine($"Waiting for the API at {apiUrl}");
await api.WaitUntilUpAsync(TimeSpan.FromMinutes(3), ct);

if (await api.SignInAsync(adminUsername, adminPassword, ct) is not { } admin)
{
    Console.WriteLine($"{adminUsername} no longer has the demo's temporary password, so someone has signed in already: the setup leaves the "
        + "plant as it is. Set up an edge node as the user guide describes (Edge nodes).");
    return 0;
}

var temporary = admin.User.MustChangePassword;
var working = temporary ? DemoPlan.NewPassword() : adminPassword;
string edgePassword;
Guid edgeNodeId;
try
{
    if (temporary)
    {
        await api.ChangePasswordAsync(adminPassword, working, ct);
        _ = await api.SignInAsync(adminUsername, working, ct) ?? throw new InvalidOperationException("The administrator's new password wasn't accepted.");
    }

    (edgeNodeId, edgePassword) = await SetUpEdgeAsync(api, ct);
}
finally
{
    if (temporary)
    {
        // Back to the demo's temporary password, whatever happened above, so the first sign-in still works as documented.
        await api.ResetPasswordAsync(admin.User.Id, adminPassword, CancellationToken.None);
        Console.WriteLine($"{adminUsername}'s temporary password is back; the first sign-in chooses a new one.");
    }
}

// The edge's own temporary password becomes a real one, as signing in once on the Setup tab's advice would.
var edgeTemporary = await api.SignInAsync(DemoPlan.EdgeUsername, edgePassword, ct)
    ?? throw new InvalidOperationException($"{DemoPlan.EdgeUsername} couldn't sign in with its temporary password.");
var edgeFinal = DemoPlan.NewPassword();
if (edgeTemporary.User.MustChangePassword)
{
    await api.ChangePasswordAsync(edgePassword, edgeFinal, ct);
}
else
{
    edgeFinal = edgePassword;
}

Directory.CreateDirectory(Path.GetDirectoryName(edgeFile)!);
await File.WriteAllTextAsync(edgeFile + ".tmp", DemoPlan.EdgeEnvironment(edgeApiUrl, edgeNodeId, DemoPlan.EdgeUsername, edgeFinal), ct);
File.Move(edgeFile + ".tmp", edgeFile, overwrite: true);
Console.WriteLine($"Wrote the edge's sign-in to {edgeFile}.");

await StartMachinesAsync(ct);
Console.WriteLine("The demo is set up: the edge collects the simulated machines' data.");
return 0;

// The edge starts once its sign-in is written; give it a moment to connect, so it records the machines starting.
async Task StartMachinesAsync(CancellationToken ct)
{
    await Task.Delay(TimeSpan.FromSeconds(15), ct);
    await new MachineStarter(mqttHost, mqttPort).StartAllAsync(DemoPlan.Machines, TimeSpan.FromMinutes(2), ct);
}

// The node, its connection, the machines' tags and the edge's user; what exists already (from an interrupted run) is kept.
async Task<(Guid EdgeNodeId, string EdgePassword)> SetUpEdgeAsync(PulaskiApi api, CancellationToken ct)
{
    var equipment = await api.ListEquipmentAsync(ct);
    var site = equipment.SingleOrDefault(e => e.Level == "site" && e.Code == siteCode)
        ?? throw new InvalidOperationException($"There is no site {siteCode}; the demo seed makes it.");

    var node = (await api.ListEdgeNodesAsync(site.Id, ct)).FirstOrDefault(n => n.Code == DemoPlan.EdgeNodeCode);
    var nodeId = node?.Id ?? await api.CreateEdgeNodeAsync(new CreateEdgeNodeRequest
    {
        SiteId = site.Id,
        Code = DemoPlan.EdgeNodeCode,
        Name = "Demo edge",
        Description = "Reads the demo's simulated machines (made by the demo setup)",
    }, ct);
    Console.WriteLine($"Edge node {DemoPlan.EdgeNodeCode}: {(node is null ? "added" : "kept")}.");

    var detail = await api.GetEdgeNodeAsync(nodeId, ct);
    var connection = detail.Connections.FirstOrDefault(c => c.Code == DemoPlan.ConnectionCode);
    var connectionId = connection?.Id ?? await api.CreateConnectionAsync(nodeId, new CreateEdgeConnectionRequest
    {
        Code = DemoPlan.ConnectionCode,
        Name = "Simulators' broker",
        Protocol = "sparkplug_b",
        Settings = new Dictionary<string, string> { ["host"] = mqttHost, ["port"] = mqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture), ["groupId"] = DemoPlan.GroupId },
    }, ct);
    Console.WriteLine($"Connection {DemoPlan.ConnectionCode} to {mqttHost}:{mqttPort}: {(connection is null ? "added" : "kept")}.");

    // The simulators number their states their own way: their profile says what each means in PackML.
    var profile = (await api.ListMachineProfilesAsync(site.Id, ct)).FirstOrDefault(m => m.Code == DemoPlan.ProfileCode);
    var profileId = profile?.Id;
    if (profileId is null)
    {
        var created = await api.CreateMachineProfileAsync(new CreateMachineProfileRequest
        {
            SiteId = site.Id,
            Code = DemoPlan.ProfileCode,
            Name = "PackML simulator",
            Vendor = "Spruiktec",
            Model = "packml-simulator",
            Description = "The demo's simulated machines (made by the demo setup)",
        }, ct);
        var imported = await api.ImportMachineProfileAsync(created.Id, created.Version, DemoPlan.ProfileCsv, ct);
        if (!imported.Applied)
        {
            throw new InvalidOperationException($"The simulators' profile wasn't loaded: {string.Join("; ", imported.FileProblems)} ({imported.Errors} rows wrong).");
        }

        profileId = created.Id;
        Console.WriteLine($"Machine profile {DemoPlan.ProfileCode} added from the simulators' file: {imported.Created} added, {imported.Updated} changed.");
    }

    foreach (var machine in DemoPlan.Machines)
    {
        var unit = equipment.SingleOrDefault(e => e.SiteId == site.Id && e.Code == machine.Device)
            ?? throw new InvalidOperationException($"There is no equipment {machine.Device} at {siteCode}.");
        if (unit.MachineProfileId != profileId)
        {
            await api.SetMachineDataAsync(unit.Id, new SetEquipmentMachineDataRequest
            {
                Version = unit.Version,
                MachineProfileId = profileId,
                StateDerivation = unit.StateDerivation,
                ConstraintEquipmentId = unit.ConstraintEquipmentId,
                MicroStopThresholdSeconds = unit.MicroStopThresholdSeconds,
                ProductionCounterEquipmentId = unit.ProductionCounterEquipmentId,
            }, ct);
            Console.WriteLine($"{machine.Device} uses profile {DemoPlan.ProfileCode}.");
        }
    }

    foreach (var tag in DemoPlan.Tags)
    {
        if (detail.Tags.Any(t => t.Address == tag.Address && t.Role == tag.Role))
        {
            continue;
        }

        var unit = equipment.Single(e => e.SiteId == site.Id && e.Code == tag.EquipmentCode);
        await api.CreateTagAsync(new CreateEquipmentTagRequest { EquipmentId = unit.Id, Role = tag.Role, ConnectionId = connectionId, Address = tag.Address }, ct);
        Console.WriteLine($"Bound {tag.Address} to {tag.EquipmentCode} as {tag.Role}.");
    }

    var password = DemoPlan.NewPassword();
    if ((await api.ListUsersAsync(ct)).FirstOrDefault(u => u.Username == DemoPlan.EdgeUsername) is { } user)
    {
        await api.ResetPasswordAsync(user.Id, password, ct);
        Console.WriteLine($"User {DemoPlan.EdgeUsername}: kept, given a new password.");
    }
    else
    {
        await api.CreateUserAsync(new CreateUserRequest
        {
            Username = DemoPlan.EdgeUsername,
            DisplayName = "Demo edge",
            TemporaryPassword = password,
            RoleCode = "edge_node",
            SiteId = site.Id,
        }, ct);
        Console.WriteLine($"User {DemoPlan.EdgeUsername} added, with the Edge node role at {siteCode}.");
    }

    return (nodeId, password);
}

static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
