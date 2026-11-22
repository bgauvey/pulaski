namespace Pulaski.Contracts.Localization;

/// <summary>
/// Every term the screens translate (#128, D-114): the English text as written in the web's markup, one per line in <c>terms.txt</c>,
/// generated from the source by <c>tools/Pulaski.Tools terms</c> and kept complete by a test. The English text is the key.
/// </summary>
public static class TermCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> Loaded = new(Load);
    private static readonly Lazy<HashSet<string>> Set = new(() => new HashSet<string>(Loaded.Value, StringComparer.Ordinal));

    public static IReadOnlyList<string> Terms => Loaded.Value;

    public static bool Contains(string term) => Set.Value.Contains(term);

    private static IReadOnlyList<string> Load()
    {
        using var stream = typeof(TermCatalog).Assembly.GetManifestResourceStream("Pulaski.Localization.terms.txt")
            ?? throw new InvalidOperationException("The term catalog isn't embedded.");
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)];
    }
}
