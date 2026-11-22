using Pulaski.Application.Localization;

namespace Pulaski.Testing;

/// <summary>In-memory languages, translations and user preferences (#128).</summary>
public sealed class InMemoryLanguages : ILanguageRepository, ILanguageReadStore, IPreferencesRepository
{
    private readonly Lock _lock = new();

    public Dictionary<string, Language> Languages { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string Code, string Term), Translation> Translations { get; } = [];

    public Dictionary<Guid, (string? Language, string? Region)> Preferences { get; } = [];

    public Task<Language?> GetForUpdateAsync(string code, CancellationToken cancellationToken) => GetAsync(code, cancellationToken);

    public Task InsertAsync(Language language, DateTimeOffset at, CancellationToken cancellationToken) => UpdateAsync(language, at, cancellationToken);

    public Task UpdateAsync(Language language, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Languages[language.Code] = language;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, Translation>> GetForUpdateAsync(string code, IReadOnlyCollection<string> terms, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyDictionary<string, Translation>>(
                Translations.Where(t => t.Key.Code == code && terms.Contains(t.Key.Term)).ToDictionary(t => t.Key.Term, t => t.Value, StringComparer.Ordinal));
        }
    }

    public Task SetAsync(string code, IReadOnlyList<(string Term, string? Text)> items, DateTimeOffset at, string by, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var (term, text) in items)
            {
                Translations[(code, term)] = new Translation(term, text, false, at, by);
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> ApplyShippedAsync(string code, string name, IReadOnlyList<(string Term, string Text)> items, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var changed = 0;
        lock (_lock)
        {
            Languages.TryAdd(code, new Language(code, name, true));
            foreach (var (term, text) in items)
            {
                if (!Translations.TryGetValue((code, term), out var current) || (current.IsShipped && current.Text != text))
                {
                    Translations[(code, term)] = new Translation(term, text, true, at, "pulaski");
                    changed++;
                }
            }
        }

        return Task.FromResult(changed);
    }

    public Task<IReadOnlyList<Language>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Language>>([.. Languages.Values.OrderBy(l => l.Name, StringComparer.Ordinal)]);
        }
    }

    public Task<Language?> GetAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Languages.GetValueOrDefault(code));
        }
    }

    public Task<IReadOnlyList<Translation>> ListTranslationsAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Translation>>([.. Translations.Where(t => t.Key.Code == code).Select(t => t.Value)]);
        }
    }

    public Task<(string? Language, string? Region)> GetUserPreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Preferences.GetValueOrDefault(userId));
        }
    }

    public Task<bool> SetAsync(Guid userId, string? language, string? region, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Preferences[userId] = (language, region);
        }

        return Task.FromResult(true);
    }
}
