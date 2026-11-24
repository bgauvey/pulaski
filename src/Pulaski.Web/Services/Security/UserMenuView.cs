namespace Pulaski.Web.Services.Security;

/// <summary>What the header's user menu shows of the signed-in user.</summary>
public static class UserMenuView
{
    /// <summary>
    /// Up to two letters for the menu's button: the first letters of the first and last words of the name ("Jo Smith" → "JS"), one letter for
    /// one word, "?" for none.
    /// </summary>
    public static string Initials(string? displayName)
    {
        var words = (displayName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => char.IsLetterOrDigit(w[0]))
            .ToArray();
        return words.Length switch
        {
            0 => "?",
            1 => char.ToUpperInvariant(words[0][0]).ToString(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0])),
        };
    }
}
