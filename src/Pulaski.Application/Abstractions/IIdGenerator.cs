namespace Pulaski.Application.Abstractions;

/// <summary>Creates UUIDv7 ids in the application (D-006).</summary>
public interface IIdGenerator
{
    Guid NewId();
}

/// <summary>UUIDv7 from the injected clock, so tests get deterministic timestamps.</summary>
public sealed class UuidV7Generator(TimeProvider timeProvider) : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}
