namespace Pulaski.Domain;

/// <summary>
/// Marks English text the server keeps for the screens to show (a permission's description, an alert kind's name) as a catalog term (#188):
/// <c>Term.Mark("See the schedule")</c> is just the text, but the term extractor finds it, so a screen shows it with <c>L[...]</c>.
/// </summary>
public static class Term
{
    public static string Mark(string term) => term;
}
