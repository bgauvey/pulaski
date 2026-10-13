namespace Pulaski.Application.Abstractions;

/// <summary>Who is acting, and the correlation id that ties their events together.</summary>
public interface IRequestContext
{
    string ActorId { get; }

    Guid CorrelationId { get; }
}
