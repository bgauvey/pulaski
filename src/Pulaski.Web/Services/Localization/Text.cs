using System.Collections.Concurrent;
using System.Globalization;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Contracts.Quality;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.Boms;

namespace Pulaski.Web.Services.Localization;

/// <summary>
/// The screens' text in the reader's language (#128, D-114): <c>L[term]</c>, or with values <c>L[term, value, …]</c> for a term with {0}, {1}…, formatted
/// in the reader's region. The English text is the key; a term with no translation shows in English. The language is the current UI culture's
/// unless a page (a terminal's) sets it with <see cref="Use"/>.
/// </summary>
public sealed class Text(TranslationCache? cache)
{
    /// <summary>English only, as the terms are written: for helpers outside a page, and tests.</summary>
    public static Text English { get; } = new(null);

    /// <summary>The API's problem titles (<c>ApplicationExceptionHandler</c>), so the catalog holds them.</summary>
    public static readonly IReadOnlyList<string> ProblemTitles =
    [
        Term.Mark("Not found"), Term.Mark("Not signed in"), Term.Mark("Not allowed"), Term.Mark("Conflict"), Term.Mark("Rule violated"),
        Term.Mark("One or more fields are invalid."),
    ];

    private string? _language;

    /// <summary>The language shown: a code such as <c>es</c>, or <c>en</c>.</summary>
    public string Language => _language ?? TranslationCache.LanguageOf(CultureInfo.CurrentUICulture);

    /// <summary>Shows this language instead of the UI culture's (a terminal's own, D-114).</summary>
    public void Use(string? language) => _language = string.IsNullOrWhiteSpace(language) ? null : language;

    /// <summary>Loads the language's translations if they aren't, or are stale; call before the first render.</summary>
    public Task EnsureAsync() => cache?.EnsureAsync(Language) ?? Task.CompletedTask;

    public string this[string term] => cache?.Find(Language, term) ?? term;

    /// <summary>
    /// A refusal from the API in the reader's language (#128): its template translated and filled in their region, else the English as sent.
    /// </summary>
    public string? Message(ApiException? error)
    {
        if (error is null)
        {
            return null;
        }

        if (error.Template is { } template)
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, this[template], [.. error.Values]);
            }
            catch (FormatException)
            {
                // A translation or values that don't fit: the English as sent.
            }
        }

        return error.Detail;
    }

    /// <summary>A term chosen in code (a helper's label, marked with <see cref="Term.Mark"/>) in the reader's language; null stays null.</summary>
    public string? Of(string? term) => term is null ? null : this[term];

    /// <summary>A status or kind code as words in the reader's language: 'in_progress' → 'In progress' (<see cref="LabelTerms"/>).</summary>
    public string Label(string? code) => this[Display.Label(code)];

    /// <summary>
    /// A refusal's field errors in the reader's language (#189): each message's template translated and filled in their region, else the English
    /// as sent. Keyed by field, in the API's order.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors(ApiException error) =>
        error.Errors.ToDictionary(
            e => e.Key,
            e => e.Value.Select((english, i) =>
                error.FieldMessages.TryGetValue(e.Key, out var messages) && i < messages.Count && messages[i].Template.Length > 0
                    ? Fill(messages[i].Template, messages[i].Values) ?? english
                    : english).ToArray(),
            StringComparer.Ordinal);

    /// <summary>A refusal's field errors as one line, in the reader's language; null when there are none.</summary>
    public string? FieldErrorLine(ApiException error) =>
        error.Errors.Count == 0 ? null : string.Join(" ", FieldErrors(error).Values.SelectMany(v => v));

    private string? Fill(string template, IReadOnlyList<object?> values)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, this[template], [.. values]);
        }
        catch (FormatException)
        {
            // A translation or values that don't fit: the English as sent.
            return null;
        }
    }

    /// <summary>A note or warning from the API in the reader's language (#189): its template translated and filled, else its English text.</summary>
    public string Of(MessageDto message) => message.Template is { Length: > 0 } template ? Fill(template, Values(message.Values)) ?? message.Text : message.Text;

    /// <inheritdoc cref="Of(MessageDto)"/>
    public string Of(InventoryWarningDto warning) =>
        warning.Template is { Length: > 0 } template ? Fill(template, Values(warning.Values)) ?? warning.Message : warning.Message;

    /// <inheritdoc cref="Of(MessageDto)"/>
    public string Of(ScheduleWarningDto warning) =>
        warning.Template is { Length: > 0 } template ? Fill(template, Values(warning.Values)) ?? warning.Message : warning.Message;

    /// <inheritdoc cref="Of(MessageDto)"/>
    public string Of(SpecProblemDto problem) => Of(problem.Message, problem.Template, problem.Values);

    /// <inheritdoc cref="Of(MessageDto)"/>
    public string Of(ProcessProblemDto problem) => Of(problem.Message, problem.Template, problem.Values);

    /// <inheritdoc cref="Of(MessageDto)"/>
    public string Of(BomProblemDto problem) => Of(problem.Message, problem.Template, problem.Values);

    /// <inheritdoc cref="Of(MessageDto)"/>
    public string Of(ProcessOrderProblemDto problem) => Of(problem.Message, problem.Template, problem.Values);

    private string Of(string english, string? template, IReadOnlyList<object?> values) =>
        template is { Length: > 0 } ? Fill(template, Values(values)) ?? english : english;

    /// <summary>Notes from the API in the reader's language, in order.</summary>
    public IReadOnlyList<string> Notes(IEnumerable<MessageDto> messages) => [.. messages.Select(Of)];

    /// <summary>A message's values as sent: JSON numbers as numbers (for the reader's region), the rest as text.</summary>
    private static IReadOnlyList<object?> Values(IReadOnlyList<object?> values) =>
        [.. values.Select(v => v is System.Text.Json.JsonElement e
            ? e.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when e.TryGetDecimal(out var number) => number,
                System.Text.Json.JsonValueKind.Number => e.GetDouble(),
                System.Text.Json.JsonValueKind.String => e.GetString(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => (object?)e.ToString(),
            }
            : v)];

    /// <summary>A refusal in one line: its message, else its title, in the reader's language.</summary>
    public string Problem(ApiException error) => Message(error) ?? Title(error);

    /// <summary>A problem's title in the reader's language: the API's titles are terms.</summary>
    public string Title(ApiException error) => this[error.Title];

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
