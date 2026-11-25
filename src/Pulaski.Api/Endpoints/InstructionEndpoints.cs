using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Instructions;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Instructions;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Work instructions (#126a, D-116): versioned text and files, released, and shown for operations, materials or lines.</summary>
internal static class InstructionEndpoints
{
    public static IEndpointRouteBuilder MapInstructionEndpoints(this IEndpointRouteBuilder routes)
    {
        var instructions = routes.MapGroup("/instructions").WithTags("Work instructions");

        instructions.MapGet("/", async Task<Ok<IReadOnlyList<InstructionSummaryDto>>> (
            Guid siteId, IQueryHandler<ListInstructionsQuery, IReadOnlyList<InstructionSummaryDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListInstructionsQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.InstructionsView);

        instructions.MapGet("/{id:guid}", async Task<Ok<InstructionDto>> (
            Guid id, IQueryHandler<GetInstructionQuery, InstructionDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetInstructionQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.WorkInstruction), Permissions.InstructionsView);

        // A file, as an attachment and never sniffed by the browser. The floor sees the files of what it runs (#126b).
        instructions.MapGet("/{id:guid}/files/{fileId:guid}", async (
            Guid id, Guid fileId, HttpResponse response, IQueryHandler<GetInstructionFileQuery, InstructionFileContent> handler, CancellationToken ct) =>
        {
            var file = await handler.HandleAsync(new GetInstructionFileQuery(id, fileId), ct);
            response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.File(file.Content, file.ContentType, file.FileName);
        })
        .RequireAny(Site.Of(SiteOf.WorkInstruction), Permissions.InstructionsView, Permissions.FloorView);

        instructions.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateInstructionRequest request, ICommandHandler<CreateInstructionCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateInstructionCommand(request.SiteId, request.Code, request.Title), ct);
            return TypedResults.Created($"/api/instructions/{created.Id}", created);
        })
        .RequirePermission(Permissions.Instructions, Site.FromBody<CreateInstructionRequest>(SiteOf.Equipment, r => r.SiteId));

        instructions.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateInstructionRequest request, ICommandHandler<UpdateInstructionCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateInstructionCommand(id, request.Version, request.Title, request.IsActive), ct)))
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction));

        instructions.MapPut("/{id:guid}/draft", async Task<Ok<VersionResponse>> (
            Guid id, SaveInstructionDraftRequest request, ICommandHandler<SaveInstructionDraftCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveInstructionDraftCommand(id, request.Version, request.Body), ct)))
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction));

        instructions.MapPost("/{id:guid}/draft", async Task<Ok<CreatedResponse>> (
            Guid id, ICommandHandler<NewInstructionDraftCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new NewInstructionDraftCommand(id), ct)))
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction));

        // A file for the draft (PDF, PNG or JPEG, at most 10 MB), as a multipart form.
        instructions.MapPost("/{id:guid}/draft/files", async Task<Created<CreatedResponse>> (
            Guid id, HttpRequest http, ICommandHandler<AddInstructionFileCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var form = await http.ReadFormAsync(ct);
            if (form.Files.Count == 0)
            {
                throw Application.Errors.ValidationException.Field("file", "Choose a file.");
            }

            var file = form.Files[0];
            if (file.Length > InstructionFileFormat.MaxBytes)
            {
                throw Application.Errors.ValidationException.Field("file", $"The file is larger than {InstructionFileFormat.MaxBytes / 1024 / 1024} MB.");
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var created = await handler.HandleAsync(new AddInstructionFileCommand(id, file.FileName, buffer.ToArray()), ct);
            return TypedResults.Created($"/api/instructions/{id}/files/{created.Id}", created);
        })
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction))
        .DisableAntiforgery()
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(InstructionFileFormat.MaxBytes + (1024 * 1024)))
        .Accepts<IFormFile>("multipart/form-data");

        instructions.MapPost("/{id:guid}/draft/files/{fileId:guid}/remove", async Task<Ok<VersionResponse>> (
            Guid id, Guid fileId, ICommandHandler<RemoveInstructionFileCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RemoveInstructionFileCommand(id, fileId), ct)))
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction));

        instructions.MapPost("/{id:guid}/release", async Task<Ok<VersionResponse>> (
            Guid id, ReleaseInstructionRequest request, ICommandHandler<ReleaseInstructionCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReleaseInstructionCommand(id, request.Version, request.Note), ct)))
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction));

        instructions.MapPost("/{id:guid}/targets", async Task<Ok<CreatedResponse>> (
            Guid id, AddInstructionTargetRequest request, ICommandHandler<AddInstructionTargetCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new AddInstructionTargetCommand(id, request.Kind, request.ProcessId, request.OperationNumber, request.MaterialId, request.EquipmentId), ct)))
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction));

        instructions.MapPost("/{id:guid}/targets/{targetId:guid}/end", async Task<Ok<VersionResponse>> (
            Guid id, Guid targetId, ICommandHandler<EndInstructionTargetCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndInstructionTargetCommand(id, targetId), ct)))
        .RequirePermission(Permissions.Instructions, Site.Of(SiteOf.WorkInstruction));

        return routes;
    }
}
