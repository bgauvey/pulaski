using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pulaski.Application.Security;

namespace Pulaski.Api.Security;

/// <summary>Issues Pulaski's own bearer tokens for local sign-in (D-037).</summary>
internal sealed class TokenIssuer(IOptions<SecurityOptions> options, TimeProvider timeProvider)
{
    public const string UserIdClaim = "uid";
    public const string MustChangeClaim = "pwd_change";

    public static SymmetricSecurityKey Key(SecurityOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));

    public (string Token, DateTimeOffset ExpiresAt) Issue(SignedInUser user)
    {
        var now = timeProvider.GetUtcNow();
        var expires = now + options.Value.TokenLifetime;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Subject),
            new(UserIdClaim, user.UserId.ToString()),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
        };
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(MustChangeClaim, "true"));
        }

        var token = new JwtSecurityToken(
            issuer: Issuers.Local,
            audience: SecurityOptions.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(Key(options.Value), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
