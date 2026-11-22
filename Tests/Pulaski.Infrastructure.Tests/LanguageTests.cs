using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Localization;
using Pulaski.Application.Security;
using Pulaski.Contracts.Localization;
using Pulaski.Contracts.Security;
using Pulaski.Domain;
using Pulaski.Domain.Security;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Languages, translations and users' language and region (#128), through the real handlers and SQL.</summary>
[Collection(DatabaseTests.Name)]
public sealed class LanguageTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..4];

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task A_language_is_added_translated_imported_and_exported_and_never_takes_an_unknown_term()
    {
        var code = "xx-" + _s;
        await db.SendAsync(new CreateLanguageCommand(code, "Testish"));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new CreateLanguageCommand(code, "Again")));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateLanguageCommand("en", "English")));

        var term = TermCatalog.Terms.First(t => t.Contains("{0}", StringComparison.Ordinal));
        var plain = TermCatalog.Terms.First(t => !t.Contains('{', StringComparison.Ordinal));
        await db.SendAsync(new SetTranslationsCommand(code, [new(plain, "Plain-t"), new(term, term.Replace("{0}", "<{0}>", StringComparison.Ordinal))]));
        var refused = await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new SetTranslationsCommand(code,
            [new("Not a term anywhere " + _s, "x"), new(term, "lost the placeholder")])));
        Assert.Equal(2, refused.Errors["Items"].Length);

        var texts = await QueryAsync<GetTranslationsQuery, TranslationsDto>(new GetTranslationsQuery(code));
        Assert.Equal(("Plain-t", code), (texts.Texts[plain], texts.Language));
        var listed = (await QueryAsync<ListLanguagesQuery, IReadOnlyList<LanguageDto>>(new ListLanguagesQuery())).Single(l => l.Code == code);
        Assert.Equal((2, TermCatalog.Terms.Count), (listed.Translated, listed.Terms));

        // Exported, then imported back with one change and one cleared: a dry run first, nothing written.
        var csv = await QueryAsync<ExportTranslationsQuery, string>(new ExportTranslationsQuery(code));
        Assert.Contains("Plain-t", csv, StringComparison.Ordinal);
        var edited = TranslationCsv.Write([(plain, "Plain-2"), (term, "")]);
        var dry = await db.SendAsync(new ImportTranslationsCommand(code, edited, DryRun: true));
        Assert.Equal((2, true), (dry.Changed, dry.DryRun));
        Assert.Equal("Plain-t", (await QueryAsync<GetTranslationsQuery, TranslationsDto>(new GetTranslationsQuery(code))).Texts[plain]);
        await db.SendAsync(new ImportTranslationsCommand(code, edited, DryRun: false));
        texts = await QueryAsync<GetTranslationsQuery, TranslationsDto>(new GetTranslationsQuery(code));
        Assert.Equal("Plain-2", texts.Texts[plain]);
        Assert.False(texts.Texts.ContainsKey(term));

        // Switched off, its screens fall back to English.
        await db.SendAsync(new UpdateLanguageCommand(code, "Testish", false));
        Assert.Empty((await QueryAsync<GetTranslationsQuery, TranslationsDto>(new GetTranslationsQuery(code))).Texts);
    }

    [Fact]
    public async Task Shipped_spanish_arrives_without_overwriting_a_plants_own_and_a_user_keeps_their_language_and_region()
    {
        await using (var scope = db.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ShippedTranslations>().ApplyAsync(CancellationToken.None);
        }

        var signIn = TermCatalog.Terms.First(t => t == "Sign in");
        Assert.Equal("Iniciar sesión", (await QueryAsync<GetTranslationsQuery, TranslationsDto>(new GetTranslationsQuery("es"))).Texts[signIn]);
        await db.SendAsync(new SetTranslationsCommand("es", [new(signIn, "Entrar " + _s)]));
        await using (var scope = db.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ShippedTranslations>().ApplyAsync(CancellationToken.None);
        }

        Assert.Equal("Entrar " + _s, (await QueryAsync<GetTranslationsQuery, TranslationsDto>(new GetTranslationsQuery("es"))).Texts[signIn]);
        await db.SendAsync(new SetTranslationsCommand("es", [new(signIn, "Iniciar sesión")]));

        var username = ("lang." + _s).ToLowerInvariant();
        await db.SendAsync(new CreateUserCommand(username, "Lu " + _s, null, "Temporary-pass-1", Roles.Administrator, null));
        UserAccess access;
        await using (var scope = db.Services.CreateAsyncScope())
        {
            access = (await scope.ServiceProvider.GetRequiredService<ISecurityReadStore>().GetAccessAsync("pulaski-local", username, DateTimeOffset.UtcNow, CancellationToken.None))!;
        }

        await db.SendAsync(new SetPreferencesCommand(access.UserId, "es", "es-mx"));
        var me = await QueryAsync<GetMeQuery, MeDto>(new GetMeQuery(access));
        Assert.Equal(("es", "es-MX"), (me.Language, me.Region));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new SetPreferencesCommand(access.UserId, "zz", null)));
        await db.SendAsync(new SetPreferencesCommand(access.UserId, "en", null));
        me = await QueryAsync<GetMeQuery, MeDto>(new GetMeQuery(access));
        Assert.Equal((null, null), (me.Language, me.Region));
    }
}
