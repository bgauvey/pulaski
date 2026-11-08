using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pulaski.Api.Security;
using Pulaski.Application.Errors;

namespace Pulaski.Api.Tests;

/// <summary>Signing in through an OpenID Connect provider (D-091): the code flow with PKCE, and what an ID token must be to be trusted.</summary>
public sealed class IdentityProviderClientTests
{
    private const string Authority = "https://idp.example/realms/plant";
    private const string Redirect = "https://mes.example/account/oidc/callback";
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "k1" };
    private readonly Clock _clock = new();

    [Fact]
    public async Task The_flow_sends_pkce_state_and_nonce_and_trusts_only_a_signed_token_for_this_client_and_sign_in()
    {
        var provider = new FakeProvider(_key);
        var client = Client(provider);

        var (url, transaction) = await client.StartAsync("keycloak", Redirect, CancellationToken.None);
        var query = Query(url);
        Assert.StartsWith($"{Authority}/protocol/openid-connect/auth?", url, StringComparison.Ordinal);
        Assert.Equal(("code", "pulaski", Redirect, "S256"), (query["response_type"], query["client_id"], query["redirect_uri"], query["code_challenge_method"]));
        Assert.DoesNotContain(query["nonce"], transaction, StringComparison.Ordinal);

        provider.Token = IdToken(query["nonce"]);
        var who = await client.CompleteAsync("keycloak", "the-code", query["state"], transaction, CancellationToken.None);
        Assert.Equal((Authority, "user-1", "Ana Ng", "ana@example.com"), (who.Issuer, who.Subject, who.Name, who.Email));

        // The code is redeemed with the secret and the verifier whose hash was the challenge.
        var form = provider.TokenRequests.Single();
        Assert.Equal(("authorization_code", "the-code", "s3cret", Redirect), (form["grant_type"], form["code"], form["client_secret"], form["redirect_uri"]));
        Assert.Equal(query["code_challenge"], Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(form["code_verifier"]))));

        // Another state, a tampered transaction, another nonce, another audience or another key: refused.
        await Assert.ThrowsAsync<UnauthorizedException>(() => client.CompleteAsync("keycloak", "c", "other", transaction, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedException>(() => client.CompleteAsync("keycloak", "c", query["state"], transaction[..^4] + "AAAA", CancellationToken.None));
        provider.Token = IdToken("another-nonce");
        await Assert.ThrowsAsync<UnauthorizedException>(() => client.CompleteAsync("keycloak", "c", query["state"], transaction, CancellationToken.None));
        provider.Token = IdToken(query["nonce"], audience: "someone-else");
        await Assert.ThrowsAsync<UnauthorizedException>(() => client.CompleteAsync("keycloak", "c", query["state"], transaction, CancellationToken.None));
        provider.Token = IdToken(query["nonce"], key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" });
        await Assert.ThrowsAsync<UnauthorizedException>(() => client.CompleteAsync("keycloak", "c", query["state"], transaction, CancellationToken.None));

        // An unverified e-mail isn't taken as theirs.
        provider.Token = IdToken(query["nonce"], emailVerified: false);
        Assert.Null((await client.CompleteAsync("keycloak", "c", query["state"], transaction, CancellationToken.None)).Email);

        // After ten minutes the sign-in has to start again.
        _clock.Now = _clock.Now.AddMinutes(11);
        provider.Token = IdToken(query["nonce"]);
        await Assert.ThrowsAsync<UnauthorizedException>(() => client.CompleteAsync("keycloak", "c", query["state"], transaction, CancellationToken.None));
    }

    [Fact]
    public async Task Only_a_configured_provider_and_a_callback_address_are_accepted()
    {
        var client = Client(new FakeProvider(_key));

        await Assert.ThrowsAsync<NotFoundException>(() => client.StartAsync("okta", Redirect, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => client.StartAsync("keycloak", "https://evil.example/steal", CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => client.StartAsync("keycloak", "http://mes.example/account/oidc/callback", CancellationToken.None));
        Assert.True(IdentityProviderClient.IsAllowedRedirect("http://localhost:5090/account/oidc/callback"));
        Assert.Equal([("keycloak", "Plant sign-in")], client.Providers);
    }

    private IdentityProviderClient Client(FakeProvider provider) => new(
        new Clients(provider),
        Options.Create(new SecurityOptions
        {
            SigningKey = new string('k', 40),
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["keycloak"] = new IdentityProviderOptions { DisplayName = "Plant sign-in", Authority = Authority, ClientId = "pulaski", ClientSecret = "s3cret" },
                ["half"] = new IdentityProviderOptions { Authority = Authority, ClientId = "pulaski" },
            },
        }),
        _clock);

    private string IdToken(string nonce, string audience = "pulaski", RsaSecurityKey? key = null, bool emailVerified = true) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = audience,
            IssuedAt = _clock.Now.UtcDateTime,
            NotBefore = _clock.Now.UtcDateTime,
            Expires = _clock.Now.AddMinutes(5).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "user-1",
                ["nonce"] = nonce,
                ["name"] = "Ana Ng",
                ["email"] = "ana@example.com",
                ["email_verified"] = emailVerified,
            },
            SigningCredentials = new SigningCredentials(key ?? _key, SecurityAlgorithms.RsaSha256),
        });

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A provider's discovery document, keys and token endpoint.</summary>
    private sealed class FakeProvider(RsaSecurityKey key) : HttpMessageHandler
    {
        public string Token { get; set; } = "";

        public List<Dictionary<string, string>> TokenRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Json(new Dictionary<string, object>
                {
                    ["issuer"] = Authority,
                    ["authorization_endpoint"] = $"{Authority}/protocol/openid-connect/auth",
                    ["token_endpoint"] = $"{Authority}/protocol/openid-connect/token",
                    ["jwks_uri"] = $"{Authority}/protocol/openid-connect/certs",
                });
            }

            if (path.EndsWith("/certs", StringComparison.Ordinal))
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
                return Json(new { keys = new[] { new { kty = "RSA", kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            TokenRequests.Add(body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' '))));
            return Json(new { access_token = "at", token_type = "Bearer", id_token = Token });
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    }
}
