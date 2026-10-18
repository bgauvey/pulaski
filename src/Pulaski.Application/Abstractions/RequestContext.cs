namespace Pulaski.Application.Abstractions;

/// <summary>Who is acting, and the correlation id that ties their events together.</summary>
public interface IRequestContext
{
    string ActorId { get; }

    /// <summary>The signed-in user's id (D-037); null when no one is signed in, as in tests and background work.</summary>
    Guid? UserId => null;

    Guid CorrelationId { get; }
}
