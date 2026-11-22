using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Localization;

/// <summary>
/// Pulaski's starter translations (#128, D-114), shipped as <c>Localization/Shipped/{code}.csv</c> (<c>term,translation</c>, as exported) and
/// added to the database at API start: a language and terms that are missing are added, shipped texts not changed here are updated, and
/// nothing a plant has edited or cleared is touched. Terms no longer in the catalog are left out.
/// </summary>
public sealed class ShippedTranslations(IUnitOfWork unitOfWork, ILanguageRepository languages, TimeProvider time)
{
    /// <summary>The languages Pulaski ships, by code, with their names as their speakers write them.</summary>
    public static IReadOnlyDictionary<string, string> Languages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["es"] = "Español",
    };

    /// <returns>How many translations were added or changed.</returns>
    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        var changed = 0;
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        foreach (var (code, name) in Languages)
        {
            var items = Read(code)
                .Where(i => Contracts.Localization.TermCatalog.Contains(i.Term) && i.Text is { Length: > 0 }
                    && LanguageRules.KeepsPlaceholders(i.Term, i.Text))
                .Select(i => (i.Term, i.Text!))
                .ToList();
            changed += await languages.ApplyShippedAsync(code, name, items, time.GetUtcNow(), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    /// <summary>A shipped language's file, read as the import reads it.</summary>
    public static IReadOnlyList<Contracts.Localization.TranslationInput> Read(string code)
    {
        using var stream = typeof(ShippedTranslations).Assembly.GetManifestResourceStream($"Pulaski.Localization.Shipped.{code}.csv");
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);
        return TranslationCsv.Read(reader.ReadToEnd());
    }
}
