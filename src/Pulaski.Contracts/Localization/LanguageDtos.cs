namespace Pulaski.Contracts.Localization;

/// <summary>A language the screens can show (#128): its code (such as <c>es</c>), name, and how much of the catalog it translates.</summary>
public sealed record LanguageDto
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Catalog terms with a translation, and the catalog's size.</summary>
    public int Translated { get; init; }
    public int Terms { get; init; }
}

/// <summary>A catalog term and its translation in one language; null when it has none (the English shows).</summary>
public sealed record TranslationTermDto
{
    public required string Term { get; init; }
    public string? Text { get; init; }

    /// <summary>Pulaski's shipped starter text, not changed here.</summary>
    public bool IsShipped { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}

/// <summary>What the screens need to show a language: every translated term (#128).</summary>
public sealed record TranslationsDto
{
    public required string Language { get; init; }
    public IReadOnlyDictionary<string, string> Texts { get; init; } = new Dictionary<string, string>();
}

public sealed record CreateLanguageRequest
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record UpdateLanguageRequest
{
    public string Name { get; init; } = "";
    public bool IsActive { get; init; }
}

/// <summary>Translations to set; a blank text clears one (the English shows).</summary>
public sealed record SetTranslationsRequest
{
    public IReadOnlyList<TranslationInput> Items { get; init; } = [];
}

public sealed record TranslationInput(string Term, string? Text);

/// <summary>What a translations CSV did, or would do on a dry run.</summary>
public sealed record ImportTranslationsResponse
{
    public int Changed { get; init; }
    public int Unchanged { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>The signed-in user's language and region (#128); null for each means English and the browser's region.</summary>
public sealed record PreferencesRequest
{
    public string? Language { get; init; }
    public string? Region { get; init; }
}
