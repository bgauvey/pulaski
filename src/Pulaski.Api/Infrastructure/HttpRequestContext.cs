using Pulaski.Application.Abstractions;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Actor and correlation id for the current HTTP request.
/// Until OIDC sign-in is wired up, unauthenticated callers act as <c>anonymous</c>.
/// </summary>
internal sealed class HttpRequestContext(IHttpContextAccessor accessor, IIdGenerator ids) : IRequestContext
{
    public const string CorrelationHeader = "X-Correlation-Id";

    private Guid? _correlationId;

    public string ActorId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            var subject = user?.FindFirst("sub")?.Value;
            var issuer = user?.FindFirst("iss")?.Value;
            return subject is null ? "anonymous" : $"{issuer}|{subject}";
        }
    }

    public Guid CorrelationId => _correlationId ??= ReadHeader() ?? ids.NewId();

    private Guid? ReadHeader() =>
        Guid.TryParse(accessor.HttpContext?.Request.Headers[CorrelationHeader], out var id) ? id : null;
}
