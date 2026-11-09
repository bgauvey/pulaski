using System.Text.RegularExpressions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Application.Security;
using Pulaski.Domain;
using Pulaski.Domain.Security;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Security;

/// <summary>Resetting a forgotten password by e-mail (D-093).</summary>
public partial class PasswordResetTests
{
    private const string Password = "Test-password-1";
    private readonly HandlerFixture _f = new();
    private readonly PlainPasswordHasher _hasher = new();
    private readonly FakePasswordResetSettings _settings = new();
    private readonly FakeMailOutbox _mail = new();
    private readonly SecurityUser _jo;

    public PasswordResetTests()
    {
        var site = Guid.CreateVersion7();
        Security.AddSite(site);
        Security.AddUser("admin", Password, Roles.Administrator);
        _jo = WithEmail(Security.AddUser("jo", Password, Roles.Operator, site), "Jo@Example.com");
    }

    private InMemorySecurity Security => _f.Stores.Security;

    private Task<string> RequestAsync(string login) =>
        new RequestPasswordResetHandler(_f.Stores.UnitOfWork, Security, _settings, _mail, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new RequestPasswordResetCommand(login), CancellationToken.None);

    private Task<VersionResponse> CompleteAsync(string token, string password = "Brand-new-pass-9") =>
        new CompletePasswordResetHandler(_f.Stores.UnitOfWork, Security, _settings, _hasher, _mail, _f.Stores.Events, _f.Events, _f.Time)
            .HandleAsync(new CompletePasswordResetCommand(token, password), CancellationToken.None);

    private SignInHandler SignIn() => new(_f.Stores.UnitOfWork, Security, _hasher, _f.Stores.Events, _f.Events, _f.Time);

    private SecurityUser WithEmail(SecurityUser user, string email)
    {
        var current = Security.GetUserForUpdateAsync(user.Id, CancellationToken.None).Result!;
        var updated = current.Value with { Email = email };
        Security.UpdateUserAsync(updated, HandlerFixture.Now, CancellationToken.None).Wait();
        return updated;
    }

    private string LastToken() => TokenPattern().Match(_mail.Sent[^1].Body).Groups[1].Value;

    [GeneratedRegex(@"account/reset\?token=([A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();

    [Fact]
    public async Task A_link_goes_to_the_address_on_file_works_once_and_only_its_hash_is_kept()
    {
        var unknown = await RequestAsync("nobody");
        var answer = await RequestAsync("jo@example.com");

        Assert.Equal(unknown, answer);
        var (recipient, subject, body) = Assert.Single(_mail.Sent);
        Assert.Equal(("Jo@Example.com", "Choose a new Pulaski password"), (recipient, subject));
        Assert.StartsWith("https://mes.example/account/reset?token=", body.Split('\n').Single(l => l.Contains("token=", StringComparison.Ordinal)).Trim(), StringComparison.Ordinal);
        var token = LastToken();
        var kept = Assert.Single(Security.PasswordResets).Link;
        Assert.NotEqual(token, kept.TokenHash);
        Assert.Equal(HandlerFixture.Now.AddMinutes(30), kept.ExpiresAt);
        Assert.Equal(EventTypes.PasswordResetRequested, _f.Stores.Events.Appended[^1].Type);
        Assert.DoesNotContain(token, _f.Stores.Events.Appended[^1].Data.ToString() ?? "", StringComparison.Ordinal);

        await Assert.ThrowsAsync<ValidationException>(() => CompleteAsync(token, "short"));
        await CompleteAsync(token);
        Assert.False((await SignIn().HandleAsync(new SignInCommand("jo", "Brand-new-pass-9"), CancellationToken.None)).MustChangePassword);
        Assert.Equal("Your Pulaski password was changed", _mail.Sent[^1].Subject);

        Assert.Contains("expired or was already used", (await Assert.ThrowsAsync<DomainRuleException>(() => CompleteAsync(token, "Another-pass-77"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_link_ends_the_old_one_links_expire_and_asking_too_often_sends_nothing_more()
    {
        await RequestAsync("jo");
        var first = LastToken();

        // Within a minute, nothing more is sent, and the answer doesn't say so.
        await RequestAsync("jo");
        Assert.Single(_mail.Sent);

        _f.Time.Advance(TimeSpan.FromMinutes(2));
        await RequestAsync("JO");
        var second = LastToken();
        await Assert.ThrowsAsync<DomainRuleException>(() => CompleteAsync(first));

        _f.Time.Advance(TimeSpan.FromMinutes(2));
        await RequestAsync("jo");
        _f.Time.Advance(TimeSpan.FromMinutes(2));
        await RequestAsync("jo");
        Assert.Equal(3, _mail.Sent.Count);

        _f.Time.Advance(TimeSpan.FromMinutes(31));
        await Assert.ThrowsAsync<DomainRuleException>(() => CompleteAsync(LastToken()));
        await Assert.ThrowsAsync<DomainRuleException>(() => CompleteAsync(second));
    }

    [Fact]
    public async Task Machines_shared_addresses_people_without_one_and_switched_off_reset_are_all_answered_alike()
    {
        var edge = WithEmail(Security.AddUser("edge1", Password, Roles.EdgeNode, null), "ops@example.com");
        await Security.UpdateUserAsync(edge with { IsServiceAccount = true }, HandlerFixture.Now, CancellationToken.None);
        WithEmail(Security.AddUser("bo", Password, Roles.Administrator), "shared@example.com");
        WithEmail(Security.AddUser("cy", Password, Roles.Administrator), "shared@example.com");

        await RequestAsync("edge1");
        await RequestAsync("shared@example.com");
        await RequestAsync("admin");
        Assert.Empty(_mail.Sent);

        // A user name still finds someone whose address is shared.
        await RequestAsync("bo");
        Assert.Single(_mail.Sent);

        // When only administrators use passwords (D-091), only they may reset one.
        _settings.AdministratorsOnly = true;
        _f.Time.Advance(TimeSpan.FromMinutes(2));
        await RequestAsync("jo");
        Assert.Single(_mail.Sent);

        // Switched off by default: the reset is refused as not offered.
        _settings.Enabled = false;
        await Assert.ThrowsAsync<NotFoundException>(() => RequestAsync("jo"));
    }

    [Fact]
    public async Task An_administrators_reset_or_a_password_change_ends_open_links()
    {
        await RequestAsync("jo");
        var token = LastToken();
        await new ResetPasswordHandler(_f.Stores.UnitOfWork, Security, _hasher, _f.Stores.Events, _f.Events, _f.Time)
            .HandleAsync(new ResetPasswordCommand(_jo.Id, "Temporary-pass-2"), CancellationToken.None);

        await Assert.ThrowsAsync<DomainRuleException>(() => CompleteAsync(token));
        Assert.NotNull(Assert.Single(Security.PasswordResets).RevokedAt);
    }
}
