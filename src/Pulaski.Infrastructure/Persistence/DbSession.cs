using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;

namespace Pulaski.Infrastructure.Persistence;

/// <summary>
/// The connection and transaction for one command (one DI scope). Repositories and the event
/// log run their SQL through it, so everything a command writes commits or rolls back together.
/// </summary>
internal sealed class DbSession(NpgsqlDataSource dataSource) : IUnitOfWork, IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;

    public async Task<ITransaction> BeginAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A unit of work is already open in this scope.");
        }

        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        _transaction = await _connection.BeginTransactionAsync(cancellationToken);
        return new Transaction(this);
    }

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? args, CancellationToken cancellationToken) =>
        RunAsync(sql, args, (connection, command) => connection.QuerySingleOrDefaultAsync<T>(command), cancellationToken);

    public Task<T> ExecuteScalarAsync<T>(string sql, object? args, CancellationToken cancellationToken) =>
        RunAsync(
            sql,
            args,
            async (connection, command) => await connection.ExecuteScalarAsync<T>(command)
                ?? throw new InvalidOperationException("The statement returned no value."),
            cancellationToken);

    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? args, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<T>>(
            sql, args, async (connection, command) => (await connection.QueryAsync<T>(command)).AsList(), cancellationToken);

    public Task<int> ExecuteAsync(string sql, object? args, CancellationToken cancellationToken) =>
        RunAsync(sql, args, (connection, command) => connection.ExecuteAsync(command), cancellationToken);

    /// <summary>Runs code that issues several statements of its own on the session's connection and transaction.</summary>
    public async Task<T> UseConnectionAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> run)
    {
        if (_connection is null || _transaction is null)
        {
            throw new InvalidOperationException("Writes must run inside a unit of work; call IUnitOfWork.BeginAsync first.");
        }

        try
        {
            return await run(_connection, _transaction);
        }
        catch (PostgresException ex) when (PostgresErrors.TryTranslate(ex, out var translated))
        {
            throw translated;
        }
    }

    public async ValueTask DisposeAsync() => await EndAsync(commit: false, CancellationToken.None);

    private Task<T> RunAsync<T>(
        string sql,
        object? args,
        Func<NpgsqlConnection, CommandDefinition, Task<T>> run,
        CancellationToken cancellationToken) =>
        UseConnectionAsync((connection, transaction) =>
            run(connection, new CommandDefinition(sql, args, transaction, cancellationToken: cancellationToken)));

    private async ValueTask EndAsync(bool commit, CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            return;
        }

        try
        {
            if (commit)
            {
                await _transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await _transaction.RollbackAsync(CancellationToken.None);
            }
        }
        finally
        {
            await _transaction.DisposeAsync();
            await _connection!.DisposeAsync();
            _transaction = null;
            _connection = null;
        }
    }

    private sealed class Transaction(DbSession session) : ITransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            session.EndAsync(commit: true, cancellationToken).AsTask();

        // Rolls back if CommitAsync was not reached; a no-op after commit.
        public ValueTask DisposeAsync() => session.EndAsync(commit: false, CancellationToken.None);
    }
}
