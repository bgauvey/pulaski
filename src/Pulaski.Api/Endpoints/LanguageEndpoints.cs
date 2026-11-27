using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Localization;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Localization;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Languages and translations (#128, D-114), and the signed-in user's language and region.</summary>
internal static class LanguageEndpoints
{
    public static IEndpointRouteBuilder MapLanguageEndpoints(this IEndpointRouteBuilder routes)
    {
        var languages = routes.MapGroup("/languages").WithTags("Languages");

        // Anyone signed in chooses from these.
        languages.MapGet("/", async Task<Ok<IReadOnlyList<LanguageDto>>> (IQueryHandler<ListLanguagesQuery, IReadOnlyList<LanguageDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListLanguagesQuery(), ct)))
        .RequireAnyView();

        languages.MapPost("/", async Task<Ok<VersionResponse>> (CreateLanguageRequest request, ICommandHandler<CreateLanguageCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CreateLanguageCommand(request.Code, request.Name), ct)))
        .RequirePermission(Permissions.Languages);

        languages.MapPut("/{code}", async Task<Ok<VersionResponse>> (string code, UpdateLanguageRequest request, ICommandHandler<UpdateLanguageCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateLanguageCommand(code, request.Name, request.IsActive), ct)))
        .RequirePermission(Permissions.Languages);

        languages.MapGet("/{code}/terms", async Task<Ok<IReadOnlyList<TranslationTermDto>>> (
            string code, IQueryHandler<GetLanguageTermsQuery, IReadOnlyList<TranslationTermDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLanguageTermsQuery(code), ct)))
        .RequirePermission(Permissions.Languages);

        languages.MapPut("/{code}/translations", async Task<Ok<VersionResponse>> (
            string code, SetTranslationsRequest request, ICommandHandler<SetTranslationsCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetTranslationsCommand(code, request.Items), ct)))
        .RequirePermission(Permissions.Languages);

        languages.MapGet("/{code}/translations.csv", async Task<FileContentHttpResult> (string code, IQueryHandler<ExportTranslationsQuery, string> handler, CancellationToken ct) =>
            TypedResults.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(await handler.HandleAsync(new ExportTranslationsQuery(code), ct))).ToArray(),
                "text/csv", $"translations-{code}.csv"))
        .RequirePermission(Permissions.Languages);

        // All or nothing; dryRun says what would change.
        languages.MapPost("/{code}/import", async Task<Ok<ImportTranslationsResponse>> (
            string code, bool? dryRun, HttpRequest request, ICommandHandler<ImportTranslationsCommand, ImportTranslationsResponse> handler, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            return TypedResults.Ok(await handler.HandleAsync(new ImportTranslationsCommand(code, await reader.ReadToEndAsync(ct), dryRun ?? false), ct));
        })
        .Accepts<string>("text/csv", "text/plain")
        .RequirePermission(Permissions.Languages);

        // What the screens show in a language: open to all, so the sign-in page and the terminals can use it too. Translations aren't secret.
        routes.MapGet("/translations/{code}", [AllowSignedOut] async Task<Ok<TranslationsDto>> (
            string code, IQueryHandler<GetTranslationsQuery, TranslationsDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetTranslationsQuery(code), ct)))
        .WithTags("Languages");

        routes.MapPut("/me/preferences", async Task<Ok<VersionResponse>> (
            PreferencesRequest request, HttpContext http, ICommandHandler<SetPreferencesCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetPreferencesCommand((await CallerAccess.RequireAsync(http)).UserId, request.Language, request.Region), ct)))
        .WithTags("Languages")
        .RequireAnyView();

        // A user's own view settings (#131): filters, favourites, home page; only their own, whatever they may see.
        routes.MapPut("/me/settings", async Task<Ok<VersionResponse>> (
            Contracts.Security.UserSettingRequest request, HttpContext http, ICommandHandler<Application.Security.SetUserSettingCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new Application.Security.SetUserSettingCommand((await CallerAccess.RequireAsync(http)).UserId, request.Key, request.Value), ct)))
        .WithTags("Security")
        .RequireAnyView();

        return routes;
    }
}
