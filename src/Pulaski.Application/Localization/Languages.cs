using System.Globalization;
using System.Text.RegularExpressions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Localization;
using Pulaski.Domain;

namespace Pulaski.Application.Localization;

/// <summary>A language as the commands see it (#128, D-114).</summary>
public sealed record Language(string Code, string Name, bool IsActive);

/// <summary>A translation as stored: null text when cleared.</summary>
public sealed record Translation(string Term, string? Text, bool IsShipped, DateTimeOffset? UpdatedAt, string? UpdatedBy);

/// <summary>Language writes (#128), inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ILanguageRepository
{
    Task<Language?> GetForUpdateAsync(string code, CancellationToken cancellationToken);

    Task InsertAsync(Language language, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateAsync(Language language, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The language's translations of these terms, locked.</summary>
    Task<IReadOnlyDictionary<string, Translation>> GetForUpdateAsync(string code, IReadOnlyCollection<string> terms, CancellationToken cancellationToken);

    /// <summary>Sets each term's text (null clears it), no longer shipped.</summary>
    Task SetAsync(string code, IReadOnlyList<(string Term, string? Text)> items, DateTimeOffset at, string by, CancellationToken cancellationToken);

    /// <summary>
    /// Pulaski's starter translations for a language: the language added when missing (switched on), each term added when missing, and
    /// changed when still the shipped text; a translation edited here is never overwritten.
    /// </summary>
    /// <returns>How many translations were added or changed.</returns>
    Task<int> ApplyShippedAsync(string code, string name, IReadOnlyList<(string Term, string Text)> items, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Language reads (#128).</summary>
public interface ILanguageReadStore
{
    Task<IReadOnlyList<Language>> ListAsync(CancellationToken cancellationToken);

    Task<Language?> GetAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<Translation>> ListTranslationsAsync(string code, CancellationToken cancellationToken);

    /// <summary>A user's language and region; nulls when they chose none (or there is no such user).</summary>
    Task<(string? Language, string? Region)> GetUserPreferencesAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Adds a language to translate into (#128); English is the source and is never added.</summary>
public sealed record CreateLanguageCommand(string Code, string Name) : ICommand<VersionResponse>;

/// <summary>Renames a language, or switches it off (its translations are kept; nobody can choose it).</summary>
public sealed record UpdateLanguageCommand(string Code, string Name, bool IsActive) : ICommand<VersionResponse>;

/// <summary>Sets translations of catalog terms; a blank text clears one.</summary>
public sealed record SetTranslationsCommand(string Code, IReadOnlyList<TranslationInput> Items) : ICommand<VersionResponse>;

/// <summary>Imports a CSV of <c>term,translation</c> (as exported): all or nothing; with <paramref name="DryRun"/> it only says what would change.</summary>
public sealed record ImportTranslationsCommand(string Code, string Csv, bool DryRun) : ICommand<ImportTranslationsResponse>;

/// <summary>The languages, with how much of the catalog each translates.</summary>
public sealed record ListLanguagesQuery : IQuery<IReadOnlyList<LanguageDto>>;

/// <summary>Every catalog term with its translation in the language, in catalog order.</summary>
public sealed record GetLanguageTermsQuery(string Code) : IQuery<IReadOnlyList<TranslationTermDto>>;

/// <summary>What the screens need for a language: its translated terms; none for English, an unknown or a switched-off language.</summary>
public sealed record GetTranslationsQuery(string Code) : IQuery<TranslationsDto>;

/// <summary>The language's translations as a CSV of <c>term,translation</c>, every catalog term, blank where untranslated.</summary>
public sealed record ExportTranslationsQuery(string Code) : IQuery<string>;

/// <summary>Sets the signed-in user's language and region (#128).</summary>
public sealed record SetPreferencesCommand(Guid UserId, string? Language, string? Region) : ICommand<VersionResponse>;

/// <summary>A user's language and region (#128).</summary>
public interface IPreferencesRepository
{
    Task<bool> SetAsync(Guid userId, string? language, string? region, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>The rules for languages, regions and translations (#128). Pure.</summary>
public static partial class LanguageRules
{
    public const string English = "en";
    public const int MaxText = 4000;
    public const string CsvTerm = "term";
    public const string CsvTranslation = "translation";

    /// <summary>A language code such as <c>es</c> or <c>pt-BR</c>, lower-case language; English is the source, never added.</summary>
    public static string Code(string? code)
    {
        var trimmed = (code ?? "").Trim();
        if (!CodePattern().IsMatch(trimmed))
        {
            throw new DomainRuleException("A language code is two or three letters, optionally with a region, such as es or pt-BR.");
        }

        return trimmed.Split('-') is [var language, .. var rest] ? string.Join('-', [language.ToLowerInvariant(), .. rest]) : trimmed;
    }

    /// <summary>A region (a culture name, for dates and numbers), such as <c>es-MX</c>; null for none.</summary>
    public static string? Region(string? region)
    {
        var trimmed = region?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(trimmed, predefinedOnly: true);
            return culture.IsNeutralCulture ? throw new DomainRuleException($"Choose a region, such as {trimmed}-US, not only a language.") : culture.Name;
        }
        catch (CultureNotFoundException)
        {
            throw new DomainRuleException($"{trimmed} isn't a region this server knows, such as en-US or es-MX.");
        }
    }

    /// <summary>The event subject for a language: the same id for the same code, always.</summary>
    public static Guid SubjectOf(string code) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("language:" + code)).AsSpan(0, 16));

    public static string? Text(string? text)
    {
        var trimmed = text?.Trim();
        if (trimmed is { Length: > MaxText })
        {
            throw new DomainRuleException($"Keep a translation to {MaxText} characters.");
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Whether a translation keeps the term's placeholders (<c>{0}</c>, <c>{1}</c> …): each must appear, and no other, so a screen never
    /// loses a value or fails to show.
    /// </summary>
    public static bool KeepsPlaceholders(string term, string text) =>
        Placeholders(term).SetEquals(Placeholders(text)) && !BadBrace().IsMatch(BraceFree(text));

    private static HashSet<string> Placeholders(string text) => [.. PlaceholderPattern().Matches(text).Select(m => m.Value)];

    private static string BraceFree(string text) => PlaceholderPattern().Replace(text.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal), "");

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\{\d+(,-?\d+)?(:[^{}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"[{}]")]
    private static partial Regex BadBrace();
}

internal sealed class CreateLanguageHandler(IUnitOfWork unitOfWork, ILanguageRepository languages, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<CreateLanguageCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(CreateLanguageCommand command, CancellationToken cancellationToken)
    {
        var code = LanguageRules.Code(command.Code);
        var name = (command.Name ?? "").Trim();
        if (code == LanguageRules.English || code.StartsWith(LanguageRules.English + "-", StringComparison.Ordinal))
        {
            throw new DomainRuleException("English is the language the screens are written in; add another language to translate into.");
        }

        if (name.Length is 0 or > 100)
        {
            throw new DomainRuleException("Give the language a name of at most 100 characters, as its speakers write it, such as Español.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (await languages.GetForUpdateAsync(code, cancellationToken) is not null)
        {
            throw new ConflictException($"There is already a language {code}.");
        }

        var envelope = events.Create(EventTypes.LanguageAdded, "language", LanguageRules.SubjectOf(code), new { Code = code, Name = name });
        await languages.InsertAsync(new Language(code, name, true), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

internal sealed class UpdateLanguageHandler(IUnitOfWork unitOfWork, ILanguageRepository languages, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateLanguageCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateLanguageCommand command, CancellationToken cancellationToken)
    {
        var name = (command.Name ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            throw new DomainRuleException("Give the language a name of at most 100 characters.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await languages.GetForUpdateAsync(command.Code, cancellationToken) ?? throw new NotFoundException($"There is no language {command.Code}.");
        var updated = current with { Name = name, IsActive = command.IsActive };
        if (updated != current)
        {
            var envelope = events.Create(EventTypes.LanguageChanged, "language", LanguageRules.SubjectOf(current.Code), new { Before = current, After = updated });
            await languages.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new VersionResponse(0);
    }
}

/// <summary>Checks and writes translations, for the page and the CSV import alike.</summary>
internal static class TranslationWrites
{
    /// <returns>The items that change something, after checking every one; refuses them all if any is wrong.</returns>
    public static async Task<IReadOnlyList<(string Term, string? Text)>> CheckAsync(
        ILanguageRepository languages, string code, IReadOnlyList<TranslationInput> items, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!TermCatalog.Contains(item.Term))
            {
                problems.Add($"\"{Short(item.Term)}\" isn't a term the screens use.");
                continue;
            }

            string? text;
            try
            {
                text = LanguageRules.Text(item.Text);
            }
            catch (DomainRuleException ex)
            {
                problems.Add($"\"{Short(item.Term)}\": {ex.Message}");
                continue;
            }

            if (text is not null && !LanguageRules.KeepsPlaceholders(item.Term, text))
            {
                problems.Add($"\"{Short(item.Term)}\": the translation must keep the placeholders {{0}}, {{1}}… of the English, and no others.");
                continue;
            }

            if (!wanted.TryAdd(item.Term, text))
            {
                problems.Add($"\"{Short(item.Term)}\" is given twice.");
            }
        }

        if (problems.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Items"] = [.. problems.Take(20)] });
        }

        var current = await languages.GetForUpdateAsync(code, [.. wanted.Keys], cancellationToken);
        return [.. wanted.Where(w => current.GetValueOrDefault(w.Key)?.Text != w.Value && !(w.Value is null && !current.ContainsKey(w.Key))).Select(w => (w.Key, w.Value))];
    }

    private static string Short(string term) => term.Length > 60 ? term[..57] + "…" : term;
}

internal sealed class SetTranslationsHandler(IUnitOfWork unitOfWork, ILanguageRepository languages, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetTranslationsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetTranslationsCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        _ = await languages.GetForUpdateAsync(command.Code, cancellationToken) ?? throw new NotFoundException($"There is no language {command.Code}.");
        var changes = await TranslationWrites.CheckAsync(languages, command.Code, command.Items, cancellationToken);
        if (changes.Count > 0)
        {
            var envelope = events.Create(EventTypes.TranslationsSet, "language", LanguageRules.SubjectOf(command.Code), new { Changes = changes.Select(c => new { c.Term, c.Text }) });
            await languages.SetAsync(command.Code, changes, envelope.OccurredAt, envelope.ActorId, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new VersionResponse(0);
    }
}

internal sealed class ImportTranslationsHandler(IUnitOfWork unitOfWork, ILanguageRepository languages, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<ImportTranslationsCommand, ImportTranslationsResponse>
{
    public async Task<ImportTranslationsResponse> HandleAsync(ImportTranslationsCommand command, CancellationToken cancellationToken)
    {
        var items = TranslationCsv.Read(command.Csv);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        _ = await languages.GetForUpdateAsync(command.Code, cancellationToken) ?? throw new NotFoundException($"There is no language {command.Code}.");
        var changes = await TranslationWrites.CheckAsync(languages, command.Code, items, cancellationToken);
        if (!command.DryRun && changes.Count > 0)
        {
            var envelope = events.Create(EventTypes.TranslationsSet, "language", LanguageRules.SubjectOf(command.Code), new { Imported = changes.Count });
            await languages.SetAsync(command.Code, changes, envelope.OccurredAt, envelope.ActorId, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new ImportTranslationsResponse { Changed = changes.Count, Unchanged = items.Count - changes.Count, DryRun = command.DryRun };
    }
}

/// <summary>The translations CSV (#128): <c>term,translation</c>, as exported; rows with a blank term are skipped.</summary>
public static class TranslationCsv
{
    public static IReadOnlyList<TranslationInput> Read(string csv)
    {
        var records = CsvText.Read(csv ?? "");
        if (records.Count == 0)
        {
            throw new DomainRuleException("The file is empty; export the language first to see the columns.");
        }

        var header = records[0].Fields.Select(CsvText.Normalize).ToList();
        var term = header.IndexOf(LanguageRules.CsvTerm);
        var translation = header.IndexOf(LanguageRules.CsvTranslation);
        if (term < 0 || translation < 0)
        {
            throw new DomainRuleException("The first line names the columns: term,translation.");
        }

        return [.. records.Skip(1)
            .Where(r => r.Fields.Count > term && r.Fields[term].Length > 0)
            .Select(r => new TranslationInput(r.Fields[term], r.Fields.Count > translation ? r.Fields[translation] : null))];
    }

    public static string Write(IEnumerable<(string Term, string? Text)> rows) =>
        CsvText.Write([LanguageRules.CsvTerm, LanguageRules.CsvTranslation], rows.Select(r => (IReadOnlyList<string?>)[r.Term, r.Text]));
}

internal sealed class ListLanguagesHandler(ILanguageReadStore store) : IQueryHandler<ListLanguagesQuery, IReadOnlyList<LanguageDto>>
{
    public async Task<IReadOnlyList<LanguageDto>> HandleAsync(ListLanguagesQuery query, CancellationToken cancellationToken)
    {
        var result = new List<LanguageDto>();
        foreach (var language in await store.ListAsync(cancellationToken))
        {
            var translated = (await store.ListTranslationsAsync(language.Code, cancellationToken)).Count(t => t.Text is not null && TermCatalog.Contains(t.Term));
            result.Add(new LanguageDto { Code = language.Code, Name = language.Name, IsActive = language.IsActive, Translated = translated, Terms = TermCatalog.Terms.Count });
        }

        return result;
    }
}

internal sealed class GetLanguageTermsHandler(ILanguageReadStore store) : IQueryHandler<GetLanguageTermsQuery, IReadOnlyList<TranslationTermDto>>
{
    public async Task<IReadOnlyList<TranslationTermDto>> HandleAsync(GetLanguageTermsQuery query, CancellationToken cancellationToken)
    {
        _ = await store.GetAsync(query.Code, cancellationToken) ?? throw new NotFoundException($"There is no language {query.Code}.");
        var translations = (await store.ListTranslationsAsync(query.Code, cancellationToken)).ToDictionary(t => t.Term, StringComparer.Ordinal);
        return [.. TermCatalog.Terms.Select(term => translations.GetValueOrDefault(term) is { } t
            ? new TranslationTermDto { Term = term, Text = t.Text, IsShipped = t.IsShipped, UpdatedAt = t.UpdatedAt, UpdatedBy = t.UpdatedBy }
            : new TranslationTermDto { Term = term })];
    }
}

internal sealed class GetTranslationsHandler(ILanguageReadStore store) : IQueryHandler<GetTranslationsQuery, TranslationsDto>
{
    public async Task<TranslationsDto> HandleAsync(GetTranslationsQuery query, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(query.Code, cancellationToken) is not { IsActive: true } language)
        {
            return new TranslationsDto { Language = LanguageRules.English };
        }

        var texts = (await store.ListTranslationsAsync(language.Code, cancellationToken))
            .Where(t => t.Text is not null)
            .ToDictionary(t => t.Term, t => t.Text!, StringComparer.Ordinal);
        return new TranslationsDto { Language = language.Code, Texts = texts };
    }
}

internal sealed class ExportTranslationsHandler(IQueryHandler<GetLanguageTermsQuery, IReadOnlyList<TranslationTermDto>> terms) : IQueryHandler<ExportTranslationsQuery, string>
{
    public async Task<string> HandleAsync(ExportTranslationsQuery query, CancellationToken cancellationToken) =>
        TranslationCsv.Write((await terms.HandleAsync(new GetLanguageTermsQuery(query.Code), cancellationToken)).Select(t => (t.Term, t.Text)));
}

internal sealed class SetPreferencesHandler(IUnitOfWork unitOfWork, IPreferencesRepository preferences, ILanguageReadStore languages, TimeProvider time)
    : ICommandHandler<SetPreferencesCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetPreferencesCommand command, CancellationToken cancellationToken)
    {
        var language = string.IsNullOrWhiteSpace(command.Language) ? null : LanguageRules.Code(command.Language);
        if (language == LanguageRules.English)
        {
            language = null;
        }
        else if (language is not null && await languages.GetAsync(language, cancellationToken) is not { IsActive: true })
        {
            throw new DomainRuleException($"{language} isn't a language the screens are translated into.");
        }

        var region = LanguageRules.Region(command.Region);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await preferences.SetAsync(command.UserId, language, region, time.GetUtcNow(), cancellationToken))
        {
            throw new NotFoundException("That user wasn't found.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}
