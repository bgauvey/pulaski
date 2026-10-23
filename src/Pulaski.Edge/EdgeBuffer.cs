using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Edge;

/// <summary>
/// The edge's store-and-forward buffer (D-050): a local SQLite file. Every signal is written here before it is forwarded, under
/// an increasing sequence number, and removed only once Pulaski has applied it, so a network drop or a restart loses nothing.
/// The file's stream id tells Pulaski when the buffer was lost and its numbering starts again. The last configuration is kept
/// too, so the edge keeps collecting when it starts without Pulaski.
/// </summary>
public sealed class EdgeBuffer : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public EdgeBuffer(string path)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS signal (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                tag_id TEXT NOT NULL,
                occurred_at INTEGER NOT NULL,
                value TEXT,
                good INTEGER NOT NULL);
            """);
        StreamId = Guid.TryParse(Meta("stream_id"), out var stream) ? stream : NewStream();
    }

    /// <summary>This buffer's stream: a new file is a new stream.</summary>
    public Guid StreamId { get; }

    public long Count
    {
        get
        {
            lock (_lock)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM signal";
                return (long)command.ExecuteScalar()!;
            }
        }
    }

    /// <summary>Writes a signal; its sequence number is the buffer's next.</summary>
    public long Append(Guid tagId, DateTimeOffset occurredAt, decimal? value, bool good)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO signal (tag_id, occurred_at, value, good) VALUES ($tag, $at, $value, $good) RETURNING seq";
            command.Parameters.AddWithValue("$tag", tagId.ToString());
            command.Parameters.AddWithValue("$at", occurredAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$value", value is { } v ? v.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("$good", good ? 1 : 0);
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>The oldest signals not yet forwarded, in order.</summary>
    public IReadOnlyList<EdgeSignalDto> Take(int count)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT seq, tag_id, occurred_at, value, good FROM signal ORDER BY seq LIMIT $count";
            command.Parameters.AddWithValue("$count", count);
            using var reader = command.ExecuteReader();
            var signals = new List<EdgeSignalDto>();
            while (reader.Read())
            {
                signals.Add(new EdgeSignalDto
                {
                    Sequence = reader.GetInt64(0),
                    TagId = Guid.Parse(reader.GetString(1)),
                    OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    Value = reader.IsDBNull(3) ? null : decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    Good = reader.GetInt64(4) == 1,
                });
            }

            return signals;
        }
    }

    /// <summary>Drops everything Pulaski has applied: every signal up to <paramref name="sequence"/>.</summary>
    public void Remove(long sequence)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM signal WHERE seq <= $seq";
            command.Parameters.AddWithValue("$seq", sequence);
            command.ExecuteNonQuery();
        }
    }

    public void SaveConfig(EdgeConfigDto config) => SetMeta("config", JsonSerializer.Serialize(config));

    public EdgeConfigDto? LoadConfig() => Meta("config") is { } json ? JsonSerializer.Deserialize<EdgeConfigDto>(json) : null;

    public void Dispose() => _connection.Dispose();

    private Guid NewStream()
    {
        var stream = Guid.CreateVersion7();
        SetMeta("stream_id", stream.ToString());
        return stream;
    }

    private string? Meta(string key)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    private void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
