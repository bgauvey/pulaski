using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;

namespace Pulaski.Testing;

/// <summary>In-memory material connections (D-068).</summary>
public sealed class InMemoryMaterialConnections : IMaterialConnectionStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<MaterialConnection>> _rows = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, string? By, string? Summary)> _runs = [];

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

    public Task RecordRunAsync(Guid id, string actor, string summary, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs[id] = (at, actor, summary);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, (DateTimeOffset At, string? By, string? Summary)>> LastRunsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, (DateTimeOffset At, string? By, string? Summary)>>(new Dictionary<Guid, (DateTimeOffset, string?, string?)>(_runs));
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
