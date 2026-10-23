using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;

namespace Pulaski.Api.Infrastructure;

/// <summary>Actor and correlation id for the current HTTP request (D-037): the signed-in user (<see cref="Issuers.ActorId"/>).</summary>
internal sealed class HttpRequestContext(IHttpContextAccessor accessor, IIdGenerator ids) : IRequestContext
{
    public const string CorrelationHeader = "X-Correlation-Id";

    private Guid? _correlationId;

    public string ActorId
    {
        get
        {
            // At a shop-floor terminal (D-049) the terminal records the time, for the person who badged.
            if (accessor.HttpContext?.Items[Security.TerminalFilter.ItemKey] is Application.Labor.TerminalRecord terminal)
            {
                return terminal.ActorId;
            }

            var user = accessor.HttpContext?.User;
            var subject = user?.FindFirst("sub")?.Value;
            var issuer = user?.FindFirst("iss")?.Value;
            return subject is null ? "anonymous" : Issuers.ActorId(issuer ?? "", subject);
        }
    }

    /// <summary>The user the request's access was resolved for; local tokens also carry it.</summary>
    public Guid? UserId =>
        accessor.HttpContext is { } http && http.Items.TryGetValue("pulaski.access", out var access) && access is Domain.Security.UserAccess a
            ? a.UserId
            : Guid.TryParse(accessor.HttpContext?.User.FindFirst(TokenIssuer.UserIdClaim)?.Value, out var id) ? id : null;

    public Guid CorrelationId => _correlationId ??= ReadHeader() ?? ids.NewId();

    private Guid? ReadHeader() =>
        Guid.TryParse(accessor.HttpContext?.Request.Headers[CorrelationHeader], out var id) ? id : null;
}
