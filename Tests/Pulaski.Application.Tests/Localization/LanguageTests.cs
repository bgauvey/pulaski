using Pulaski.Application.Localization;
using Pulaski.Contracts.Localization;
using Pulaski.Domain;

namespace Pulaski.Application.Tests.Localization;

/// <summary>Languages and translations (#128).</summary>
public class LanguageTests
{
    [Fact]
    public void Codes_and_regions_are_checked_and_written_one_way()
    {
        Assert.Equal("es", LanguageRules.Code(" ES "));
        Assert.Equal("pt-BR", LanguageRules.Code("pt-BR"));
        Assert.Throws<DomainRuleException>(() => LanguageRules.Code("spanish"));
        Assert.Throws<DomainRuleException>(() => LanguageRules.Code(""));

        Assert.Equal("es-MX", LanguageRules.Region("es-mx"));
        Assert.Null(LanguageRules.Region(" "));
        Assert.Throws<DomainRuleException>(() => LanguageRules.Region("es"));
        Assert.Throws<DomainRuleException>(() => LanguageRules.Region("xx-QQ"));

        Assert.Equal(LanguageRules.SubjectOf("es"), LanguageRules.SubjectOf("es"));
        Assert.NotEqual(LanguageRules.SubjectOf("es"), LanguageRules.SubjectOf("fr"));
    }

    [Fact]
    public void A_translation_keeps_exactly_the_placeholders_of_its_term()
    {
        Assert.True(LanguageRules.KeepsPlaceholders("{0} of {1}", "{0} de {1}"));
        Assert.True(LanguageRules.KeepsPlaceholders("{0} of {1}", "{1}: {0}"));
        Assert.True(LanguageRules.KeepsPlaceholders("Save", "Guardar"));
        Assert.False(LanguageRules.KeepsPlaceholders("{0} of {1}", "{0} de"));
        Assert.False(LanguageRules.KeepsPlaceholders("Save", "Guardar {0}"));
        Assert.False(LanguageRules.KeepsPlaceholders("{0} waiting", "{0 en espera"));
    }

    [Fact]
    public void The_csv_reads_as_it_is_written_and_names_its_columns()
    {
        var csv = TranslationCsv.Write([("Save", "Guardar"), ("Sign in with {0}", "Iniciar sesión con {0}"), ("Yes, \"quoted\"", null)]);
        var read = TranslationCsv.Read(csv);
        Assert.Equal(
            [new TranslationInput("Save", "Guardar"), new TranslationInput("Sign in with {0}", "Iniciar sesión con {0}"), new TranslationInput("Yes, \"quoted\"", "")],
            read);
        Assert.Throws<DomainRuleException>(() => TranslationCsv.Read("english,spanish\nSave,Guardar"));
        Assert.Throws<DomainRuleException>(() => TranslationCsv.Read(""));
    }

    [Fact]
    public void Pulaski_ships_spanish_for_every_term_the_screens_use_keeping_their_placeholders()
    {
        var spanish = ShippedTranslations.Read("es").ToDictionary(t => t.Term, t => t.Text, StringComparer.Ordinal);
        Assert.All(TermCatalog.Terms, term =>
        {
            Assert.True(spanish.TryGetValue(term, out var text) && !string.IsNullOrWhiteSpace(text), $"No Spanish for \"{term}\".");
            Assert.True(LanguageRules.KeepsPlaceholders(term, text!), $"The Spanish for \"{term}\" changes its placeholders.");
        });
    }
}
