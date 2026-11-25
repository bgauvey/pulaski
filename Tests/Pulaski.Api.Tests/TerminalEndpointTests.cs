using System.Net;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task A_station_serves_only_the_files_of_the_instructions_it_shows()
    {
        // #126b: a file of an instruction the station doesn't show (no job running on its line) isn't served, whatever the terminal knows.
        var (terminal, secret) = await EnrolledAsync();
        var instruction = Pulaski.Domain.Instructions.WorkInstruction.Create(Guid.CreateVersion7(), terminal.SiteId, "WI-T", "Setup");
        await factory.Stores.Instructions.InsertAsync(instruction, DateTimeOffset.UtcNow, CancellationToken.None);
        var content = await factory.Stores.Instructions.InsertContentAsync(Guid.CreateVersion7(), [0x25, 0x50, 0x44, 0x46], "x", CancellationToken.None);
        var file = Guid.CreateVersion7();
        await factory.Stores.Instructions.InsertFileAsync(file, Guid.CreateVersion7(), content, "setup.pdf", "application/pdf", 0, CancellationToken.None);

        var uri = new Uri($"/api/terminal/station/instructions/{instruction.Id}/files/{file}", UriKind.Relative);
        Assert.Equal(HttpStatusCode.NotFound, (await TerminalClient(secret).GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateSignedOutClient().GetAsync(uri)).StatusCode);
    }

    [Fact]
    public async Task A_terminal_hears_the_changes_at_its_site_and_no_others()
    {
        // #143b: boards and stations reload on these.
        var (terminal, secret) = await EnrolledAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/terminal/live");
        using var response = await TerminalClient(secret).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        var hub = factory.Services.GetRequiredService<Pulaski.Application.Live.LiveHub>();
        var before = hub.Subscribers;
        Guid elsewhere = Guid.CreateVersion7(), here = Guid.CreateVersion7();
        hub.Publish(new Pulaski.Contracts.Live.LiveChangeDto("pulaski.job.started.v1", "job", elsewhere, Guid.CreateVersion7()));
        hub.Publish(new Pulaski.Contracts.Live.LiveChangeDto("machine", "machine", here, terminal.SiteId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(timeout.Token);
        }
        while (line is not null && !line.StartsWith("data:", StringComparison.Ordinal));

        Assert.Contains(here.ToString(), line, StringComparison.Ordinal);
        Assert.DoesNotContain(elsewhere.ToString(), line, StringComparison.Ordinal);
        Assert.True(before >= 1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateSignedOutClient().GetAsync(new Uri("/api/terminal/live", UriKind.Relative))).StatusCode);
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
