using Dapper;
using Npgsql;
using Pulaski.Application.Localization;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Localization;

/// <summary>Language writes (#128, D-114), in the command's transaction.</summary>
internal sealed class LanguageRepository(DbSession session) : ILanguageRepository, IPreferencesRepository
{
    public Task<Language?> GetForUpdateAsync(string code, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Language>("SELECT code, name, is_active FROM core.language WHERE code = @Code FOR UPDATE", new { Code = code }, cancellationToken);

    public Task InsertAsync(Language language, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "INSERT INTO core.language (code, name, is_active, created_at, updated_at) VALUES (@Code, @Name, @IsActive, @At, @At)",
            new { language.Code, language.Name, language.IsActive, At = at }, cancellationToken);

    public Task UpdateAsync(Language language, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "UPDATE core.language SET name = @Name, is_active = @IsActive, updated_at = @At WHERE code = @Code",
            new { language.Code, language.Name, language.IsActive, At = at }, cancellationToken);

    public async Task<IReadOnlyDictionary<string, Translation>> GetForUpdateAsync(string code, IReadOnlyCollection<string> terms, CancellationToken cancellationToken) =>
        (await session.QueryAsync<Translation>(
            """
            SELECT term, text, is_shipped, updated_at, updated_by FROM core.translation
            WHERE language_code = @Code AND term = ANY(@Terms)
            ORDER BY term
            FOR UPDATE
            """,
            new { Code = code, Terms = terms.ToArray() }, cancellationToken))
        .ToDictionary(t => t.Term, StringComparer.Ordinal);

    public Task SetAsync(string code, IReadOnlyList<(string Term, string? Text)> items, DateTimeOffset at, string by, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.translation (language_code, term, text, is_shipped, updated_at, updated_by)
            SELECT @Code, t.term, t.text, false, @At, @By FROM unnest(@Terms, @Texts) AS t(term, text)
            ON CONFLICT (language_code, term) DO UPDATE SET text = EXCLUDED.text, is_shipped = false, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
            """,
            new { Code = code, Terms = items.Select(i => i.Term).ToArray(), Texts = items.Select(i => i.Text).ToArray(), At = at, By = by },
            cancellationToken);

    public async Task<int> ApplyShippedAsync(string code, string name, IReadOnlyList<(string Term, string Text)> items, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync(
            "INSERT INTO core.language (code, name, is_active, created_at, updated_at) VALUES (@Code, @Name, true, @At, @At) ON CONFLICT (code) DO NOTHING",
            new { Code = code, Name = name, At = at }, cancellationToken);

        // Added when missing; changed only while still the shipped text, never once someone here has edited or cleared it.
        return await session.ExecuteAsync(
            """
            INSERT INTO core.translation AS c (language_code, term, text, is_shipped, updated_at, updated_by)
            SELECT @Code, t.term, t.text, true, @At, 'pulaski' FROM unnest(@Terms, @Texts) AS t(term, text)
            ON CONFLICT (language_code, term) DO UPDATE SET text = EXCLUDED.text, updated_at = EXCLUDED.updated_at
            WHERE c.is_shipped AND c.text IS DISTINCT FROM EXCLUDED.text
            """,
            new { Code = code, Terms = items.Select(i => i.Term).ToArray(), Texts = items.Select(i => i.Text).ToArray(), At = at },
            cancellationToken);
    }

    public async Task<bool> SetAsync(Guid userId, string? language, string? region, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.ExecuteAsync(
            "UPDATE sec.app_user SET language = @Language, region = @Region, updated_at = @At WHERE id = @Id",
            new { Id = userId, Language = language, Region = region, At = at }, cancellationToken) == 1;
}

/// <summary>Language reads (#128, D-114).</summary>
internal sealed class LanguageQueries(NpgsqlDataSource dataSource) : ILanguageReadStore
{
    public async Task<IReadOnlyList<Language>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<Language>(new CommandDefinition(
            "SELECT code, name, is_active FROM core.language ORDER BY is_active DESC, name", cancellationToken: cancellationToken))];
    }

    public async Task<Language?> GetAsync(string code, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Language>(new CommandDefinition(
            "SELECT code, name, is_active FROM core.language WHERE code = @Code", new { Code = code }, cancellationToken: cancellationToken));
    }

    public async Task<(string? Language, string? Region)> GetUserPreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<(string?, string?)>(new CommandDefinition(
            "SELECT language, region FROM sec.app_user WHERE id = @Id", new { Id = userId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Translation>> ListTranslationsAsync(string code, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<Translation>(new CommandDefinition(
            "SELECT term, text, is_shipped, updated_at, updated_by FROM core.translation WHERE language_code = @Code", new { Code = code }, cancellationToken: cancellationToken))];
    }
}
