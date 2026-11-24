using System.Reflection;
using System.Runtime.ExceptionServices;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pulaski.Application.Abstractions;

namespace Pulaski.DemoHistory;

/// <summary>The clock the handlers read: set to each moment of the history as it is recorded, whole microseconds like the application's.</summary>
public sealed class HistoryClock : TimeProvider
{
    private DateTimeOffset _now = MicrosecondClock.Instance.GetUtcNow();

    public DateTimeOffset Now => _now;

    public void Set(DateTimeOffset at) => _now = new DateTimeOffset(at.UtcTicks - (at.UtcTicks % 10), TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Who is acting: one of the demo's people by their username, as a signed-in user would (their actor id is the username).</summary>
public sealed class Acting : IRequestContext
{
    public string ActorId { get; set; } = "system";

    public Guid? UserId { get; set; }

    public Guid CorrelationId { get; set; } = Guid.CreateVersion7();
}

/// <summary>Runs commands through their real handlers, each in its own scope (one unit of work), as the API does.</summary>
public sealed class Commands(IServiceProvider services, Acting acting, HistoryClock clock)
{
    private readonly Dictionary<string, Guid> _users = new(StringComparer.Ordinal);

    public void KnowUser(string username, Guid id) => _users[username] = id;

    public async Task<TResult> SendAsync<TResult>(string actor, ICommand<TResult> command)
    {
        acting.ActorId = actor;
        acting.UserId = _users.TryGetValue(actor, out var id) ? id : null;
        acting.CorrelationId = Guid.CreateVersion7(clock.Now);
        await using var scope = services.CreateAsyncScope();
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        var handler = scope.ServiceProvider.GetRequiredService(handlerType);
        var handle = handlerType.GetMethod("HandleAsync")!;
        try
        {
            return await (Task<TResult>)handle.Invoke(handler, [command, CancellationToken.None])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
}

/// <summary>Reads what the history needs to know (ids, versions) straight from the database, as the application's login.</summary>
public sealed class Lookup(NpgsqlDataSource db)
{
    public async Task<Guid> IdAsync(string sql, object? args = null)
    {
        await using var connection = await db.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<Guid?>(sql, args) ?? throw new InvalidOperationException($"Nothing found for: {sql}");
    }

    public async Task<T?> OneAsync<T>(string sql, object? args = null)
    {
        await using var connection = await db.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<T>(sql, args);
    }

    public async Task<IReadOnlyList<T>> ListAsync<T>(string sql, object? args = null)
    {
        await using var connection = await db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<T>(sql, args)];
    }

    public Task<Guid> EquipmentAsync(string code) => IdAsync("SELECT id FROM core.equipment WHERE code = @code", new { code });

    public Task<Guid> MaterialAsync(string code) => IdAsync("SELECT id FROM core.material WHERE code = @code", new { code });

    public Task<Guid> ReasonAsync(string kind, string code) =>
        IdAsync("SELECT id FROM core.reason_code WHERE reason_kind = @kind AND code = @code AND site_id IS NULL ORDER BY is_active DESC LIMIT 1", new { kind, code });

    public Task<long> VersionAsync(string table, Guid id) =>
        OneAsync<long>($"SELECT xmin::text::bigint FROM {table} WHERE id = @id", new { id });
}
