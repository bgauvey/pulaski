using System.Text.Json;
using Pulaski.Contracts.MachineData;
using Pulaski.Web.Services.MachineData;

namespace Pulaski.Web.Tests;

/// <summary>The edge node screens' helpers (D-041).</summary>
public class EdgeViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void When_a_node_was_last_heard_from_reads_naturally()
    {
        Assert.Equal("never", EdgeView.LastHeard(null, Now, null));
        Assert.Equal("just now", EdgeView.LastHeard(Now.AddSeconds(-20), Now, null));
        Assert.Equal("12 min ago", EdgeView.LastHeard(Now.AddMinutes(-12), Now, null));
        Assert.Equal("2026-10-18 09:00", EdgeView.LastHeard(Now.AddHours(-3), Now, "UTC"));
        Assert.True(EdgeView.IsQuiet(Now.AddMinutes(-3), Now));
        Assert.False(EdgeView.IsQuiet(Now.AddSeconds(-30), Now));
    }

    [Fact]
    public void The_setup_snippet_names_the_node_and_a_password_slot_for_each_broker_with_a_user()
    {
        var node = new EdgeNodeDetailDto
        {
            Node = new EdgeNodeDto { Id = Guid.CreateVersion7(), SiteId = Guid.CreateVersion7(), Code = "GW-1", Name = "Gateway" },
            Connections =
            [
                new() { Id = Guid.CreateVersion7(), EdgeNodeId = Guid.Empty, Code = "SECURE", Name = "Secure", Protocol = "sparkplug_b", IsActive = true,
                        Settings = new Dictionary<string, string> { ["host"] = "b", ["username"] = "edge" } },
                new() { Id = Guid.CreateVersion7(), EdgeNodeId = Guid.Empty, Code = "OPEN", Name = "Open", Protocol = "sparkplug_b", IsActive = true,
                        Settings = new Dictionary<string, string> { ["host"] = "a" } },
            ],
        };

        var snippet = EdgeView.ConfigSnippet(node, new Uri("https://pulaski.example/"));
        Assert.Contains("\"<its password>\"", snippet, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(snippet);
        var edge = json.RootElement.GetProperty("Edge");
        Assert.Equal(node.Node.Id.ToString(), edge.GetProperty("EdgeNodeId").GetString());
        Assert.Equal("https://pulaski.example/", edge.GetProperty("ApiUrl").GetString());
        Assert.Equal(["SECURE"], edge.GetProperty("Connections").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Counter_roles_take_a_rollover()
    {
        Assert.True(EdgeView.IsCounter("processed_count"));
        Assert.False(EdgeView.IsCounter("state"));
        Assert.Equal("Stop reason (first out)", EdgeView.RoleLabel("stop_reason"));
        Assert.Equal("96.48", EdgeView.Reading(96.48171m));
    }
}
