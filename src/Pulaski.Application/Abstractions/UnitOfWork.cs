namespace Pulaski.Application.Abstractions;

/// <summary>
/// One database transaction per command. Repositories and the event log enlist in it, so the
/// state change and its event commit together (D-007). Disposing without committing rolls back.
/// </summary>
public interface IUnitOfWork
{
    Task<ITransaction> BeginAsync(CancellationToken cancellationToken);
}

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Row lock taken when a command reads an aggregate inside its transaction.</summary>
public enum RowLock
{
    /// <summary>Others may read but not change or delete the row (e.g. a parent being referenced).</summary>
    Share,

    /// <summary>Exclusive: the command is about to change the row.</summary>
    Update,
}

/// <summary>A row as read, with its concurrency version (PostgreSQL <c>xmin</c>).</summary>
public sealed record Versioned<T>(T Value, long Version);
