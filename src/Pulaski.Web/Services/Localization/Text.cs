using System.Collections.Concurrent;
using System.Globalization;

namespace Pulaski.Web.Services.Localization;

/// <summary>
/// The screens' text in the reader's language (#128, D-114): <c>L[term]</c>, or with values <c>L[term, value, …]</c> for a term with {0}, {1}…, formatted
/// in the reader's region. The English text is the key; a term with no translation shows in English. The language is the current UI culture's
/// unless a page (a terminal's) sets it with <see cref="Use"/>.
/// </summary>
public sealed class Text(TranslationCache cache)
{
    private string? _language;

    /// <summary>The language shown: a code such as <c>es</c>, or <c>en</c>.</summary>
    public string Language => _language ?? TranslationCache.LanguageOf(CultureInfo.CurrentUICulture);

    /// <summary>Shows this language instead of the UI culture's (a terminal's own, D-114).</summary>
    public void Use(string? language) => _language = string.IsNullOrWhiteSpace(language) ? null : language;

    /// <summary>Loads the language's translations if they aren't, or are stale; call before the first render.</summary>
    public Task EnsureAsync() => cache.EnsureAsync(Language);

    public string this[string term] => cache.Find(Language, term) ?? term;

    public string this[string term, params object?[] values]
    {
        get
        {
            var text = this[term];
            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, values);
            }
            catch (FormatException)
            {
                return string.Format(CultureInfo.CurrentCulture, term, values);
            }
        }
    }
}

/// <summary>
/// Each language's translations, fetched from the API and kept for a minute, shared by every reader (#128). Translations are open to all, so it
/// asks without anyone's session, through its own client.
/// </summary>
public sealed class TranslationCache(IHttpClientFactory http, TimeProvider time)
{
    public const string Client = "translations";

    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, string> Texts, DateTimeOffset LoadedAt)> _languages = new(StringComparer.Ordinal);

    /// <summary>A culture's language as the translations name it: <c>es</c> for es-MX, <c>pt-BR</c> kept whole when it is the language.</summary>
    public static string LanguageOf(CultureInfo culture) => culture.Name.Length == 0 ? "en" : culture.Name.StartsWith("en", StringComparison.Ordinal) ? "en" : culture.Name;

    public string? Find(string language, string term)
    {
        if (language == "en")
        {
            return null;
        }

        if (_languages.TryGetValue(language, out var found) || _languages.TryGetValue(Neutral(language), out found))
        {
            return found.Texts.GetValueOrDefault(term);
        }

        return null;
    }

    public async Task EnsureAsync(string language)
    {
        if (language == "en")
        {
            return;
        }

        var now = time.GetUtcNow();
        if (_languages.TryGetValue(language, out var held) && now - held.LoadedAt < Fresh)
        {
            return;
        }

        try
        {
            var client = http.CreateClient(Client);
            var loaded = await GetAsync(client, language);
            if (loaded.Count == 0 && Neutral(language) != language)
            {
                loaded = await GetAsync(client, Neutral(language));
            }

            _languages[language] = (loaded, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Keeps what it had (or English) and tries again next time.
            _languages.TryAdd(language, (new Dictionary<string, string>(), now - Fresh));
        }
    }

    private static string Neutral(string language) => language.Split('-')[0];

    private static async Task<IReadOnlyDictionary<string, string>> GetAsync(HttpClient client, string language) =>
        (await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<Pulaski.Contracts.Localization.TranslationsDto>(
            client, $"api/translations/{Uri.EscapeDataString(language)}", System.Text.Json.JsonSerializerOptions.Web))?.Texts
        ?? new Dictionary<string, string>();
}
