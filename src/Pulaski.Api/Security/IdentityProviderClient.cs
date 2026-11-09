using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Pulaski.Application.Errors;

namespace Pulaski.Api.Security;

/// <summary>Who a provider vouched for (D-091): the ID token's issuer and subject, with the name and e-mail it gave.</summary>
internal sealed record ProviderIdentity(string Issuer, string Subject, string? Name, string? Email);

/// <summary>
/// The API's OpenID Connect client (D-091): the authorization code flow with PKCE (S256), as a confidential client. Starting returns the
/// provider's authorization address and a sealed transaction (state, nonce, code verifier, redirect address, 10 minutes) for the UI to keep;
/// completing checks the state, redeems the code at the token endpoint, and validates the ID token (signature from the provider's keys,
/// issuer, audience = client id, lifetime, nonce). Provider settings come from configuration only.
/// </summary>
internal sealed class IdentityProviderClient(IHttpClientFactory clients, IOptions<SecurityOptions> options, TimeProvider timeProvider)
{
    public const string ClientName = "identity-providers";
    public const string CallbackPath = "/account/oidc/callback";

    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, IConfigurationManager<OpenIdConnectConfiguration>> _metadata = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The usable providers, by name.</summary>
    public IReadOnlyList<(string Name, string DisplayName)> Providers =>
        [.. options.Value.Providers.Where(p => p.Value.IsUsable).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Key, p.Value.DisplayName.Length > 0 ? p.Value.DisplayName : p.Key))];

    public async Task<(string AuthorizationUrl, string Transaction)> StartAsync(string name, string redirectUri, CancellationToken cancellationToken)
    {
        var provider = Provider(name);
        if (!IsAllowedRedirect(redirectUri))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["RedirectUri"] = [$"An https address ending in {CallbackPath} (http only on this machine)."],
            });
        }

        var metadata = await MetadataAsync(name, provider, cancellationToken);
        var state = Random();
        var nonce = Random();
        var verifier = Random();
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = provider.Scopes,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var separator = metadata.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = metadata.AuthorizationEndpoint + separator + string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        var transaction = new Transaction(name.ToLowerInvariant(), state, nonce, verifier, redirectUri, timeProvider.GetUtcNow().Add(TransactionLifetime).ToUnixTimeSeconds());
        return (url, Seal(transaction));
    }

    public async Task<ProviderIdentity> CompleteAsync(string name, string code, string state, string sealedTransaction, CancellationToken cancellationToken)
    {
        var provider = Provider(name);
        var transaction = Unseal(sealedTransaction);
        if (transaction is null || !string.Equals(transaction.Provider, name, StringComparison.OrdinalIgnoreCase)
            || transaction.ExpiresAt < timeProvider.GetUtcNow().ToUnixTimeSeconds()
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(transaction.State), Encoding.UTF8.GetBytes(state ?? "")))
        {
            throw new UnauthorizedException("The sign-in took too long or didn't come from this page. Start again.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new UnauthorizedException($"{DisplayName(name, provider)} didn't sign you in.");
        }

        var metadata = await MetadataAsync(name, provider, cancellationToken);
        var idToken = await RedeemAsync(name, provider, metadata, code, transaction, cancellationToken);
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = metadata.Issuer,
            ValidAudience = provider.ClientId,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        });
        if (!validated.IsValid)
        {
            throw new UnauthorizedException($"{DisplayName(name, provider)}'s answer couldn't be trusted ({validated.Exception?.GetType().Name ?? "invalid token"}).");
        }

        var claims = validated.Claims;
        string? Claim(string type) => claims.TryGetValue(type, out var value) ? value?.ToString() : null;
        if (Claim("nonce") is not { } nonce || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(transaction.Nonce)))
        {
            throw new UnauthorizedException($"{DisplayName(name, provider)}'s answer wasn't for this sign-in. Start again.");
        }

        var subject = Claim("sub") ?? throw new UnauthorizedException($"{DisplayName(name, provider)}'s answer didn't say who signed in.");

        // An address the provider says it hasn't verified isn't taken as theirs.
        var email = string.Equals(Claim("email_verified"), "false", StringComparison.OrdinalIgnoreCase) ? null : Claim(provider.EmailClaim);
        return new ProviderIdentity(metadata.Issuer, subject, Claim(provider.NameClaim), email);
    }

    /// <summary>The provider's display name, or its configured name.</summary>
    public string DisplayName(string name) => Provider(name) is var p && p.DisplayName.Length > 0 ? p.DisplayName : name;

    /// <summary>An https address ending in the UI's callback path; http only on the API's own machine, for trying it out.</summary>
    public static bool IsAllowedRedirect(string redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath.EndsWith(CallbackPath, StringComparison.Ordinal)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static string DisplayName(string name, IdentityProviderOptions provider) => provider.DisplayName.Length > 0 ? provider.DisplayName : name;

    private IdentityProviderOptions Provider(string name) =>
        options.Value.Providers.TryGetValue(name, out var provider) && provider.IsUsable
            ? provider
            : throw new NotFoundException($"No identity provider '{name}'.");

    private Task<OpenIdConnectConfiguration> MetadataAsync(string name, IdentityProviderOptions provider, CancellationToken cancellationToken)
    {
        var manager = _metadata.GetOrAdd(name, _ =>
        {
            var authority = provider.Authority.TrimEnd('/');
            var loopback = Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.IsLoopback;
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{authority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(clients.CreateClient(ClientName)) { RequireHttps = !loopback });
        });
        return manager.GetConfigurationAsync(cancellationToken);
    }

    private async Task<string> RedeemAsync(
        string name, IdentityProviderOptions provider, OpenIdConnectConfiguration metadata, string code, Transaction transaction, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = transaction.RedirectUri,
                ["client_id"] = provider.ClientId,
                ["client_secret"] = provider.ClientSecret,
                ["code_verifier"] = transaction.Verifier,
            }),
        };
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
            {
                var error = document.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                throw new UnauthorizedException($"{DisplayName(name, provider)} refused the sign-in ({(int)response.StatusCode}{(error is null ? "" : $", {error}")}).");
            }

            return document.RootElement.TryGetProperty("id_token", out var token) && token.GetString() is { Length: > 0 } idToken
                ? idToken
                : throw new UnauthorizedException($"{DisplayName(name, provider)} sent no ID token; is 'openid' in the scopes?");
        }
        catch (HttpRequestException ex)
        {
            throw new UnauthorizedException($"Couldn't reach {DisplayName(name, provider)}: {ex.Message}");
        }
        catch (JsonException)
        {
            throw new UnauthorizedException($"{DisplayName(name, provider)}'s answer isn't JSON.");
        }
    }

    // ---- The transaction, sealed with AES-GCM under a key derived from the signing key, so any API instance can open it. ----

    private byte[] Key() => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(options.Value.SigningKey), 32, info: "pulaski-oidc-transaction"u8.ToArray());

    private string Seal(Transaction transaction)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(transaction);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(), tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        return Base64UrlEncoder.Encode([.. nonce, .. tag, .. cipher]);
    }

    private Transaction? Unseal(string text)
    {
        try
        {
            var bytes = Base64UrlEncoder.DecodeBytes(text);
            if (bytes.Length < 29)
            {
                return null;
            }

            var plain = new byte[bytes.Length - 28];
            using (var aes = new AesGcm(Key(), 16))
            {
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            }

            return JsonSerializer.Deserialize<Transaction>(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private sealed record Transaction(string Provider, string State, string Nonce, string Verifier, string RedirectUri, long ExpiresAt);
}
