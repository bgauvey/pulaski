using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Labor;
using Pulaski.Testing;

namespace Pulaski.Api.Tests;

/// <summary>Terminals authenticate with their own secret, only on their own endpoints, and act through badge tickets (D-049).</summary>
public sealed class TerminalEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(TerminalRecord Terminal, string Secret)> EnrolledAsync()
    {
        var site = Guid.CreateVersion7();
        var terminal = new TerminalRecord(Guid.CreateVersion7(), site, "T-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "Line terminal", site, null,
            TerminalAuthModes.Badge, true, null);
        await factory.Stores.Terminals.InsertAsync(terminal, DateTimeOffset.UtcNow, CancellationToken.None);
        var code = TerminalRules.NewEnrolmentCode();
        await factory.Stores.Terminals.SetEnrolmentCodeAsync(terminal.Id, TerminalRules.Sha256(TerminalRules.NormalizeCode(code)), DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow, CancellationToken.None);

        var response = await factory.CreateSignedOutClient().PostAsJsonAsync("/api/terminal/enrol", new EnrolTerminalRequest { Code = code });
        response.EnsureSuccessStatusCode();
        return (terminal, (await response.Content.ReadFromJsonAsync<TerminalEnrolledResponse>())!.Secret);
    }

    private HttpClient TerminalClient(string secret)
    {
        var client = factory.CreateSignedOutClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Terminal", secret);
        return client;
    }

    [Fact]
    public async Task Only_an_enrolled_terminal_with_its_secret_gets_in_and_only_to_its_endpoints()
    {
        var (terminal, secret) = await EnrolledAsync();

        Assert.Equal(HttpStatusCode.OK, (await TerminalClient(secret).GetAsync(new Uri("/api/terminal", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateSignedOutClient().GetAsync(new Uri("/api/terminal", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TerminalClient("not-the-secret").GetAsync(new Uri("/api/terminal", UriKind.Relative))).StatusCode);

        // A signed-in user isn't a terminal, and a terminal isn't a user.
        var admin = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(new Uri("/api/terminal", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TerminalClient(secret).GetAsync(new Uri("/api/equipment", UriKind.Relative))).StatusCode);

        // Switched off, it is cut off at once.
        await factory.Stores.Terminals.UpdateAsync(terminal with { IsActive = false, EnrolledAt = DateTimeOffset.UtcNow }, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TerminalClient(secret).GetAsync(new Uri("/api/terminal", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_badge_gives_a_ticket_for_this_terminal_only_and_never_a_sign_in()
    {
        var (terminal, secret) = await EnrolledAsync();
        factory.Stores.Labor.AddSite(terminal.SiteId);
        var ann = factory.Stores.Labor.AddPerson(terminal.SiteId, "Ann");
        await factory.Stores.Terminals.InsertBadgeAsync(new BadgeRecord(Guid.CreateVersion7(), ann, new ReversingBadgeHasher().Hash("BADGE77"), null,
            DateTimeOffset.UtcNow, "test", null, null, 0, null, null), CancellationToken.None);

        var unknown = await TerminalClient(secret).PostAsJsonAsync("/api/terminal/identify", new IdentifyBadgeRequest { BadgeValue = "BADGE00" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var identified = await (await TerminalClient(secret).PostAsJsonAsync("/api/terminal/identify", new IdentifyBadgeRequest { BadgeValue = "badge77" }))
            .Content.ReadFromJsonAsync<BadgeIdentityResponse>();
        Assert.Equal(("Ann", false), (identified!.Status!.PersonName, identified.NeedsPin));
        Assert.NotNull(identified.Ticket);

        // The ticket isn't a sign-in, and another terminal can't use it.
        var withTicket = factory.CreateSignedOutClient();
        withTicket.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identified.Ticket);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withTicket.GetAsync(new Uri("/api/me", UriKind.Relative))).StatusCode);

        var (_, otherSecret) = await EnrolledAsync();
        var elsewhere = await TerminalClient(otherSecret).PostAsJsonAsync("/api/terminal/breaks/start", new TerminalActionRequest { Ticket = identified.Ticket! });
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
    }
}
