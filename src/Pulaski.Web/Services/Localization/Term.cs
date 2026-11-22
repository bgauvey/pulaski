namespace Pulaski.Web.Services.Localization;

/// <summary>
/// Marks English text kept in code (a menu entry, a heading in a list) as a catalog term (#128): <c>Term.Mark("Overview")</c> is just
/// "Overview", but the term extractor finds it, so it can be translated where it is shown with <c>L[...]</c>.
/// </summary>
public static class Term
{
    public static string Mark(string term) => term;
}
