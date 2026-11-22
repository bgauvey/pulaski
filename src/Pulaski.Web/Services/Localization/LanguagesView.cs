using Pulaski.Contracts.Localization;

namespace Pulaski.Web.Services.Localization;

/// <summary>Which terms the Languages page lists (#128). Pure.</summary>
public static class LanguagesView
{
    public const int PageSize = 200;

    /// <summary>
    /// The terms shown: all, or those without a translation (counting one being typed as translated); either narrowed by a search in the English
    /// or the translation, case-insensitively.
    /// </summary>
    public static IReadOnlyList<TranslationTermDto> Filter(
        IReadOnlyList<TranslationTermDto> terms, IReadOnlyDictionary<string, string> edits, string filter, string? search) =>
        [.. terms.Where(t =>
        {
            var text = edits.TryGetValue(t.Term, out var edited) ? edited : t.Text;
            var untranslated = string.IsNullOrWhiteSpace(text);
            return (filter != "untranslated" || untranslated || edits.ContainsKey(t.Term))
                && (string.IsNullOrWhiteSpace(search)
                    || t.Term.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase)
                    || (text?.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false));
        })];
}
