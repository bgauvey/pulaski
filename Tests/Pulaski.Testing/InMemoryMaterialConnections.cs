using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;

namespace Pulaski.Testing;

/// <summary>In-memory material connections (D-068).</summary>
public sealed class InMemoryMaterialConnections : IMaterialConnectionStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<MaterialConnection>> _rows = [];

    public Task<IReadOnlyList<Versioned<MaterialConnection>>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Versioned<MaterialConnection>>>([.. _rows.Values.OrderBy(r => r.Value.Code, StringComparer.Ordinal)]);
        }
    }

    public Task<Versioned<MaterialConnection>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_rows.GetValueOrDefault(id));
        }
    }

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_rows.Values.Any(r => r.Value.Code == code));
        }
    }

    public Task<long> InsertAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken) => UpdateAsync(connection, at, cancellationToken);

    public Task<long> UpdateAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = (_rows.TryGetValue(connection.Id, out var current) ? current.Version : 0) + 1;
            _rows[connection.Id] = new(connection, version);
            return Task.FromResult(version);
        }
    }

    /// <summary>Sites the fake treats as sites, as a test sets them.</summary>
    public HashSet<Guid> Sites { get; } = [];

    /// <summary>Every run recorded, oldest first.</summary>
    public List<ConnectionRunRecord> Runs { get; } = [];

    public Task RecordRunAsync(ConnectionRunRecord run, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Runs.Add(run);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, LastConnectionRun>> LastRunsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, LastConnectionRun>>(Runs
                .GroupBy(r => r.ConnectionId)
                .ToDictionary(g => g.Key, g => g.Last() is var r ? new LastConnectionRun(r.FinishedAt, r.Actor, r.Summary, r.Outcome) : null!));
        }
    }

    public Task<IReadOnlyList<ConnectionRunRecord>> ListRunsAsync(Guid connectionId, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ConnectionRunRecord>>([.. Runs.Where(r => r.ConnectionId == connectionId).Reverse().Take(limit)]);
        }
    }

    public Task<IReadOnlyList<MaterialConnection>> ListDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MaterialConnection>>([.. _rows.Values.Select(r => r.Value)
                .Where(c => c.IsActive && c.NextRunAt is { } at && at <= now).OrderBy(c => c.NextRunAt)]);
        }
    }

    public Task<bool> ClaimAsync(Guid id, DateTimeOffset due, DateTimeOffset? following, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_rows.TryGetValue(id, out var current) || !current.Value.IsActive || current.Value.NextRunAt != due)
            {
                return Task.FromResult(false);
            }

            _rows[id] = new(current.Value with { NextRunAt = following }, current.Version + 1);
            return Task.FromResult(true);
        }
    }

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Sites.Contains(siteId));
        }
    }
}

/// <summary>A connection's items as a test sets them, by connection code, in place of an OData service.</summary>
public sealed class FakeMaterialConnectionFetcher : IMaterialConnectionFetcher
{
    public Dictionary<string, string> Json { get; } = [];

    public Dictionary<string, string> Problems { get; } = [];

    public Task<MaterialConnectionFetch> FetchAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken)
    {
        if (Problems.TryGetValue(connection.Code, out var problem))
        {
            return Task.FromResult(new MaterialConnectionFetch([], [problem]));
        }

        using var document = JsonDocument.Parse(Json.GetValueOrDefault(connection.Code) ?? "[]");
        return Task.FromResult(new MaterialConnectionFetch([.. document.RootElement.EnumerateArray().Select(e => e.Clone())], []));
    }

    public bool HasCredentials(string code) => false;
}
