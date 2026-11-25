using System.Security.Cryptography;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Instructions;
using Pulaski.Domain;
using Pulaski.Domain.Instructions;

namespace Pulaski.Application.Instructions;

/// <summary>A new work instruction at a site (#126a, D-116), with an empty first draft.</summary>
public sealed record CreateInstructionCommand(Guid SiteId, string? Code, string? Title) : ICommand<CreatedResponse>;

/// <summary>An instruction's title and whether it is shown; its code never changes.</summary>
public sealed record UpdateInstructionCommand(Guid Id, long ExpectedVersion, string? Title, bool IsActive) : ICommand<VersionResponse>;

/// <summary>The draft's text; <paramref name="ExpectedVersion"/> is the draft's own version.</summary>
public sealed record SaveInstructionDraftCommand(Guid Id, long ExpectedVersion, string? Body) : ICommand<VersionResponse>
{
    /// <summary>The draft's checklists (#126c); null keeps them.</summary>
    public IReadOnlyList<ChecklistDto>? Checklists { get; init; }
}

/// <summary>A file added to the draft: PDF, PNG or JPEG, at most 10 MB.</summary>
public sealed record AddInstructionFileCommand(Guid Id, string? FileName, byte[] Content) : ICommand<CreatedResponse>;

/// <summary>A file taken out of the draft.</summary>
public sealed record RemoveInstructionFileCommand(Guid Id, Guid FileId) : ICommand<VersionResponse>;

/// <summary>Releases the draft (whose version is <paramref name="ExpectedVersion"/>): the version released before it becomes obsolete.</summary>
public sealed record ReleaseInstructionCommand(Guid Id, long ExpectedVersion, string? Note) : ICommand<VersionResponse>;

/// <summary>A new draft from the released version's text and files.</summary>
public sealed record NewInstructionDraftCommand(Guid Id) : ICommand<CreatedResponse>;

/// <summary>Shows the instruction for an operation (a process and its operation's number), a material or a line of its site.</summary>
public sealed record AddInstructionTargetCommand(Guid Id, string? Kind, Guid? ProcessId, int? OperationNumber, Guid? MaterialId, Guid? EquipmentId)
    : ICommand<CreatedResponse>;

/// <summary>Stops showing the instruction for one of its targets.</summary>
public sealed record EndInstructionTargetCommand(Guid Id, Guid TargetId) : ICommand<VersionResponse>;

internal sealed class CreateInstructionHandler(
    IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<CreateInstructionCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateInstructionCommand command, CancellationToken cancellationToken)
    {
        var instruction = WorkInstruction.Create(ids.NewId(), command.SiteId, command.Code, command.Title);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await instructions.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        if (await instructions.CodeExistsAsync(command.SiteId, instruction.Code, cancellationToken))
        {
            throw new ConflictException($"There is already an instruction {instruction.Code} at this site.");
        }

        var now = time.GetUtcNow();
        var envelope = events.Create(EventTypes.InstructionCreated, "work_instruction", instruction.Id, new { After = instruction });
        var version = await instructions.InsertAsync(instruction, now, cancellationToken);
        await instructions.InsertVersionAsync(InstructionVersion.FirstDraft(ids.NewId(), instruction.Id), now, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(instruction.Id, version);
    }
}

internal sealed class UpdateInstructionHandler(IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<UpdateInstructionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateInstructionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken);
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var after = current.Value.Edit(command.Title, command.IsActive);
        if (after == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.InstructionChanged, "work_instruction", after.Id, new { Before = current.Value, After = after });
        var version = await instructions.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SaveInstructionDraftHandler(IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<SaveInstructionDraftCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SaveInstructionDraftCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var instruction = (await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken)).Value;
        var draft = await InstructionWriter.DraftAsync(instructions, instruction, cancellationToken);
        if (draft.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"The draft of {instruction.Code} was changed by someone else. Reload and try again.");
        }

        var after = draft.Value.SaveDraft(command.Body, command.Checklists?.Select(ChecklistMapping.ToDomain).ToList());
        if (after == draft.Value)
        {
            return new VersionResponse(draft.Version);
        }

        var envelope = events.Create(EventTypes.InstructionDraftSaved, "work_instruction", instruction.Id, new { draft.Value.Number, Length = after.Body.Length, Checklists = after.Checklists.Select(c => new { c.Phase, Items = c.Items.Count }) });
        var version = await instructions.UpdateVersionAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class AddInstructionFileHandler(
    IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<AddInstructionFileCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(AddInstructionFileCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var instruction = (await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken)).Value;
        var draft = await InstructionWriter.DraftAsync(instructions, instruction, cancellationToken);
        var files = await instructions.ListFilesAsync(draft.Value.Id, cancellationToken);
        var contentType = InstructionFiles.Check(command.FileName, command.Content, files.Count);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(command.Content));
        var contentId = await instructions.InsertContentAsync(ids.NewId(), command.Content, sha256, cancellationToken);
        var fileId = ids.NewId();
        var name = command.FileName!.Trim();
        await instructions.InsertFileAsync(fileId, draft.Value.Id, contentId, name, contentType, files.Count == 0 ? 0 : files.Max(f => f.SortOrder) + 1, cancellationToken);
        var envelope = events.Create(
            EventTypes.InstructionFileAdded, "work_instruction", instruction.Id, new { draft.Value.Number, FileId = fileId, FileName = name, ContentType = contentType, Size = command.Content.Length, Sha256 = sha256 });
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(fileId, 0);
    }
}

internal sealed class RemoveInstructionFileHandler(IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<RemoveInstructionFileCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RemoveInstructionFileCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var instruction = (await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken)).Value;
        var draft = await InstructionWriter.DraftAsync(instructions, instruction, cancellationToken);
        if (!await instructions.RemoveFileAsync(draft.Value.Id, command.FileId, time.GetUtcNow(), cancellationToken))
        {
            throw new NotFoundException("That file isn't in the draft.");
        }

        await eventLog.AppendAsync(events.Create(EventTypes.InstructionFileRemoved, "work_instruction", instruction.Id, new { draft.Value.Number, command.FileId }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

internal sealed class ReleaseInstructionHandler(IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<ReleaseInstructionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ReleaseInstructionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var instruction = (await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken)).Value;
        var draft = await InstructionWriter.DraftAsync(instructions, instruction, cancellationToken);
        if (draft.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"The draft of {instruction.Code} was changed by someone else. Reload and try again.");
        }

        var now = time.GetUtcNow();
        var files = await instructions.ListFilesAsync(draft.Value.Id, cancellationToken);
        var envelope = events.Create(EventTypes.InstructionReleased, "work_instruction", instruction.Id, new { instruction.Code, draft.Value.Number, command.Note, Files = files.Count });
        var released = draft.Value.Release(envelope.OccurredAt, envelope.ActorId, command.Note, files.Count);

        // The one released before it gives way first: a single released version at a time.
        if (await instructions.GetReleasedAsync(instruction.Id, cancellationToken) is { } before)
        {
            await instructions.UpdateVersionAsync(before.MakeObsolete(), now, cancellationToken);
        }

        var version = await instructions.UpdateVersionAsync(released, now, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class NewInstructionDraftHandler(
    IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<NewInstructionDraftCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(NewInstructionDraftCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var instruction = (await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken)).Value;
        if (await instructions.GetDraftAsync(instruction.Id, cancellationToken) is not null)
        {
            throw new DomainRuleException($"{instruction.Code} has a draft already.");
        }

        var released = await instructions.GetReleasedAsync(instruction.Id, cancellationToken)
            ?? throw new DomainRuleException($"{instruction.Code} has no released version to start from.");
        var draft = released.NextDraft(ids.NewId());
        var version = await instructions.InsertVersionAsync(draft, time.GetUtcNow(), cancellationToken);

        // The files come along, pointing at the same bytes.
        foreach (var file in await instructions.ListFilesAsync(released.Id, cancellationToken))
        {
            await instructions.InsertFileAsync(ids.NewId(), draft.Id, file.ContentId, file.FileName, file.ContentType, file.SortOrder, cancellationToken);
        }

        await eventLog.AppendAsync(events.Create(EventTypes.InstructionDraftStarted, "work_instruction", instruction.Id, new { draft.Number, From = released.Number }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(draft.Id, version);
    }
}

internal sealed class AddInstructionTargetHandler(
    IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<AddInstructionTargetCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(AddInstructionTargetCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var instruction = (await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken)).Value;
        var target = InstructionTarget.Create(ids.NewId(), instruction.Id, command.Kind, command.ProcessId, command.OperationNumber, command.MaterialId, command.EquipmentId);
        if (!await instructions.TargetExistsAsync(instruction.SiteId, target, cancellationToken))
        {
            throw target.Kind switch
            {
                InstructionTargetKinds.Operation => new DomainRuleException($"That process has no operation {target.OperationNumber} at this site."),
                InstructionTargetKinds.Material => new DomainRuleException("That material isn't one Pulaski knows."),
                _ => new DomainRuleException("That isn't a line of this site."),
            };
        }

        if ((await instructions.ListTargetsAsync(instruction.Id, cancellationToken)).Any(t => t.IsActive && t.SameAs(target)))
        {
            throw new DomainRuleException($"{instruction.Code} is shown there already.");
        }

        await instructions.InsertTargetAsync(target, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.InstructionTargetAdded, "work_instruction", instruction.Id, new { Target = target }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(target.Id, 0);
    }
}

internal sealed class EndInstructionTargetHandler(IUnitOfWork unitOfWork, IInstructionRepository instructions, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<EndInstructionTargetCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(EndInstructionTargetCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var instruction = (await InstructionWriter.LockAsync(instructions, command.Id, cancellationToken)).Value;
        if (!await instructions.EndTargetAsync(instruction.Id, command.TargetId, time.GetUtcNow(), cancellationToken))
        {
            throw new NotFoundException("That isn't one of the instruction's targets.");
        }

        await eventLog.AppendAsync(events.Create(EventTypes.InstructionTargetEnded, "work_instruction", instruction.Id, new { command.TargetId }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

/// <summary>What every change to an instruction does first (#126a).</summary>
internal static class InstructionWriter
{
    public static async Task<Versioned<WorkInstruction>> LockAsync(IInstructionRepository instructions, Guid id, CancellationToken cancellationToken) =>
        await instructions.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Work instruction {id} not found.");

    public static async Task<Versioned<InstructionVersion>> DraftAsync(IInstructionRepository instructions, WorkInstruction instruction, CancellationToken cancellationToken) =>
        await instructions.GetDraftAsync(instruction.Id, cancellationToken)
        ?? throw new DomainRuleException($"{instruction.Code} has no draft; start a new one from the released version.");
}

/// <summary>The site's work instructions (#126a), by code.</summary>
public sealed record ListInstructionsQuery(Guid SiteId) : IQuery<IReadOnlyList<InstructionSummaryDto>>;

/// <summary>One instruction with its versions, files and targets.</summary>
public sealed record GetInstructionQuery(Guid Id) : IQuery<InstructionDto>;

/// <summary>One of an instruction's files, to send.</summary>
public sealed record GetInstructionFileQuery(Guid Id, Guid FileId) : IQuery<InstructionFileContent>;

internal sealed class ListInstructionsHandler(IInstructionReadStore store) : IQueryHandler<ListInstructionsQuery, IReadOnlyList<InstructionSummaryDto>>
{
    public Task<IReadOnlyList<InstructionSummaryDto>> HandleAsync(ListInstructionsQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, cancellationToken);
}

internal sealed class GetInstructionHandler(IInstructionReadStore store) : IQueryHandler<GetInstructionQuery, InstructionDto>
{
    public async Task<InstructionDto> HandleAsync(GetInstructionQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Work instruction {query.Id} not found.");
}

internal sealed class GetInstructionFileHandler(IInstructionReadStore store) : IQueryHandler<GetInstructionFileQuery, InstructionFileContent>
{
    public async Task<InstructionFileContent> HandleAsync(GetInstructionFileQuery query, CancellationToken cancellationToken) =>
        await store.GetFileAsync(query.Id, query.FileId, cancellationToken) ?? throw new NotFoundException("That file isn't one of the instruction's.");
}

/// <summary>The released work instructions a job shows (#126b): for its process operation, its line (or one above it) and its material.</summary>
public sealed record GetJobInstructionsQuery(Guid JobId) : IQuery<IReadOnlyList<JobInstructionDto>>;

internal sealed class GetJobInstructionsHandler(IInstructionReadStore store) : IQueryHandler<GetJobInstructionsQuery, IReadOnlyList<JobInstructionDto>>
{
    public Task<IReadOnlyList<JobInstructionDto>> HandleAsync(GetJobInstructionsQuery query, CancellationToken cancellationToken) =>
        store.ForJobAsync(query.JobId, cancellationToken);
}
