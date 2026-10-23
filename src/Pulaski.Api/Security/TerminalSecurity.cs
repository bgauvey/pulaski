using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Domain.Labor;

namespace Pulaski.Api.Security;

/// <summary>Badge values hashed with a key derived from the signing key (D-049), so the stored hashes can't be guessed offline.</summary>
internal sealed class BadgeHasher(IOptions<SecurityOptions> options) : IBadgeHasher
{
    public byte[] Hash(string normalizedBadgeValue)
    {
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SigningKey), "pulaski-badge-v1"u8);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalizedBadgeValue));
    }
}

/// <summary>
/// The short-lived ticket a badge scan gives a terminal (D-049): it names the person for the next action at that terminal,
/// so the badge value isn't sent again. Signed like Pulaski's tokens but for another audience, so it is never a sign-in.
/// </summary>
internal sealed class BadgeTickets(IOptions<SecurityOptions> options, TimeProvider timeProvider)
{
    public const string Audience = "pulaski-terminal";
    private const string TerminalClaim = "term";

    public (string Ticket, DateTimeOffset ExpiresAt) Issue(Guid terminalId, Guid personId)
    {
        var now = timeProvider.GetUtcNow();
        var expires = now + TerminalRules.TicketLifetime;
        var token = new JwtSecurityToken(
            issuer: Application.Security.Issuers.Local,
            audience: Audience,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, personId.ToString()), new Claim(TerminalClaim, terminalId.ToString())],
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(TokenIssuer.Key(options.Value), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>The person the ticket names, when it is valid and was given to this terminal.</summary>
    /// <exception cref="UnauthorizedException">It isn't, or has run out: badge again.</exception>
    public Guid PersonOf(string ticket, Guid terminalId)
    {
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(ticket, new TokenValidationParameters
            {
                ValidIssuer = Application.Security.Issuers.Local,
                ValidAudience = Audience,
                IssuerSigningKey = TokenIssuer.Key(options.Value),
                ClockSkew = TimeSpan.FromSeconds(30),
                LifetimeValidator = (_, expires, _, _) => expires is { } e && e > timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-30),
            }, out _);
            if (principal.FindFirst(TerminalClaim)?.Value == terminalId.ToString() && Guid.TryParse(principal.FindFirst("sub")?.Value, out var personId))
            {
                return personId;
            }
        }
        catch (SecurityTokenException)
        {
        }
        catch (ArgumentException)
        {
        }

        throw new UnauthorizedException("That badge scan has run out; badge again.");
    }
}

/// <summary>
/// Authenticates a shop-floor terminal (D-049): <c>Authorization: Terminal &lt;secret&gt;</c>, the secret it got when it was
/// enrolled. Only an active, enrolled terminal with that secret gets through; it is kept on the request for the handlers and
/// as the actor of what it records.
/// </summary>
internal sealed class TerminalFilter : IEndpointFilter
{
    public const string Scheme = "Terminal";
    public const string ItemKey = "pulaski.terminal";

    public static TerminalRecord Current(HttpContext http) => http.Items[ItemKey] as TerminalRecord ?? throw new UnauthorizedException("Enrol this terminal first.");

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme + " ", StringComparison.Ordinal) || header.Length <= Scheme.Length + 1)
        {
            throw new UnauthorizedException("This is for enrolled terminals.");
        }

        var store = http.RequestServices.GetRequiredService<ITerminalReadStore>();
        var terminal = await store.FindBySecretAsync(TerminalRules.Sha256(header[(Scheme.Length + 1)..].Trim()), http.RequestAborted)
            ?? throw new UnauthorizedException("This terminal isn't enrolled, or was switched off. Ask a supervisor to enrol it again.");
        http.Items[ItemKey] = terminal;
        return await next(context);
    }
}
