using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Api.Security;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Tests;

/// <summary>Password reset by e-mail on the API (D-093): off by default; on, the same answer for anyone, and the e-mail sent off the request.</summary>
public sealed class PasswordResetEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task It_is_off_unless_configured_with_a_mail_server_and_the_uis_address()
    {
        var client = factory.CreateSignedOutClient();

        Assert.False((await client.GetFromJsonAsync<SignInOptionsDto>(new Uri("/api/auth/options", UriKind.Relative)))!.PasswordResetByEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/auth/password-reset", new RequestPasswordResetRequest { Login = "admin" })).StatusCode);
    }

    [Fact]
    public async Task On_anyone_gets_the_same_answer_and_the_link_is_sent_in_the_background()
    {
        var sender = new CapturingSender();
        var site = Guid.CreateVersion7();
        factory.Stores.Security.AddSite(site);
        var jo = factory.Stores.Security.AddUser($"jo-{Guid.CreateVersion7():N}"[..12], ApiFactory.Password, Roles.Operator, site);
        await factory.Stores.Security.UpdateUserAsync(jo with { Email = "jo@example.com" }, DateTimeOffset.UtcNow, CancellationToken.None);
        using var on = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:PasswordReset:Enabled", "true");
            b.UseSetting("Security:PasswordReset:UiBaseUrl", "https://mes.example");
            b.UseSetting("Email:Host", "smtp.example");
            b.UseSetting("Email:From", "pulaski@example.com");
            b.ConfigureTestServices(s => s.AddSingleton<IMailSender>(sender));
        });
        var client = on.CreateClient();

        Assert.True((await client.GetFromJsonAsync<SignInOptionsDto>(new Uri("/api/auth/options", UriKind.Relative)))!.PasswordResetByEmail);
        var nobody = await (await client.PostAsJsonAsync("/api/auth/password-reset", new RequestPasswordResetRequest { Login = "nobody-here" }))
            .Content.ReadFromJsonAsync<PasswordResetAnswer>();
        var known = await client.PostAsJsonAsync("/api/auth/password-reset", new RequestPasswordResetRequest { Login = jo.Username! });
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(nobody!.Message, (await known.Content.ReadFromJsonAsync<PasswordResetAnswer>())!.Message);

        var mail = await sender.NextAsync();
        Assert.Equal("jo@example.com", mail.Recipient);
        Assert.Contains("https://mes.example/account/reset?token=", mail.Body, StringComparison.Ordinal);

        var bad = await client.PostAsJsonAsync("/api/auth/password-reset/complete", new CompletePasswordResetRequest { Token = "not-a-token", NewPassword = "Brand-new-pass-9" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    private sealed class CapturingSender : IMailSender
    {
        private readonly TaskCompletionSource<OutgoingMail> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
        {
            _first.TrySetResult(mail);
            return Task.CompletedTask;
        }

        public Task<OutgoingMail> NextAsync() => _first.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
