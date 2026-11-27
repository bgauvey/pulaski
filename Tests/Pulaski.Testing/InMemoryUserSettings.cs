using Pulaski.Application.Security;

namespace Pulaski.Testing;

/// <summary>A user's view settings in memory (#131).</summary>
public sealed class InMemoryUserSettings : IUserSettingStore, IUserSettingRepository
{
    private readonly Dictionary<(Guid, string), string> _values = [];

    public Task<IReadOnlyDictionary<string, string>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            _values.Where(v => v.Key.Item1 == userId && v.Value != "null").ToDictionary(v => v.Key.Item2, v => v.Value, StringComparer.Ordinal));

    public Task<bool> SetAsync(Guid userId, string key, string json, DateTimeOffset at, CancellationToken cancellationToken)
    {
        _values[(userId, key)] = json;
        return Task.FromResult(true);
    }
}
