using Pulaski.Contracts.Localization;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Tests;

/// <summary>Which terms the Languages page lists (#128).</summary>
public class LanguagesViewTests
{
    private static readonly IReadOnlyList<TranslationTermDto> Terms =
    [
        new() { Term = "Save", Text = "Guardar" },
        new() { Term = "Sign in" },
        new() { Term = "Sign out", Text = "Cerrar sesión" },
    ];

    [Fact]
    public void Untranslated_counts_a_translation_being_typed_and_search_reads_both_languages()
    {
        Assert.Equal(["Sign in"], LanguagesView.Filter(Terms, new Dictionary<string, string>(), "untranslated", null).Select(t => t.Term));

        // One being typed stays listed, so it doesn't vanish while it is written.
        Assert.Equal(["Sign in"], LanguagesView.Filter(Terms, new Dictionary<string, string> { ["Sign in"] = "Iniciar" }, "untranslated", null).Select(t => t.Term));

        Assert.Equal(["Sign in", "Sign out"], LanguagesView.Filter(Terms, new Dictionary<string, string>(), "all", "sign").Select(t => t.Term));
        Assert.Equal(["Sign out"], LanguagesView.Filter(Terms, new Dictionary<string, string>(), "all", "cerrar").Select(t => t.Term));
    }
}
