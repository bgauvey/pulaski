using System.Net;
using System.Net.Http.Json;
using Pulaski.Contracts.Health;

namespace Pulaski.Api.Tests;

/// <summary>The health page needs its permission and says what is unhealthy, worst first (#141).</summary>
public sealed class SystemHealthEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task An_administrator_sees_what_is_failing_and_where_to_put_it_right()
    {
        factory.Stores.Health.EdgeNodes.Add((Guid.CreateVersion7(), "EDGE-1", "PLANT", true, DateTimeOffset.UtcNow.AddHours(-3)));
        factory.Stores.Health.Feeds.Add(("erp", true, 0, null, 2));
        var health = (await factory.CreateClient().GetFromJsonAsync<SystemHealthDto>(new Uri("/api/system/health", UriKind.Relative)))!;
        Assert.Equal("failing", health.Status);
        var edge = Assert.Single(health.Sections.Single(s => s.Key == "edge").Items);
        Assert.Equal(("failing", "PLANT · EDGE-1"), (edge.Status, edge.Name));
        Assert.StartsWith("edge-nodes/", edge.Link, StringComparison.Ordinal);
        Assert.Equal("ok", health.Sections.Single(s => s.Key == "database").Status);
    }

    [Fact]
    public async Task Without_the_permission_it_is_refused()
    {
        // A manager sees every screen of the plant, but system health is an administrator's until granted.
        var manager = await factory.CreateClientAsAsync(Pulaski.Domain.Security.Roles.Manager, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(new Uri("/api/system/health", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateSignedOutClient().GetAsync(new Uri("/api/system/health", UriKind.Relative))).StatusCode);
    }
}
