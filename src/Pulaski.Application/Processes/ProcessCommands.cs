using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Processes;
using Pulaski.Domain;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Processes;

public sealed record CreateProcessCommand(Guid SiteId, string Code, string Name, string? Description) : ICommand<CreatedResponse>;

public sealed record UpdateProcessCommand(Guid Id, long ExpectedVersion, string Name, string? Description, bool IsActive) : ICommand<VersionResponse>;

/// <summary>Replaces a draft's whole definition (D-023). The editor saves after every change.</summary>
public sealed record SaveProcessDraftCommand(Guid ProcessId, Guid VersionId, long ExpectedVersion, ProcessDefinitionDto Definition)
    : ICommand<SaveProcessDraftResponse>;

/// <summary>Releases a complete draft for orders; the version released before it becomes obsolete.</summary>
public sealed record ReleaseProcessVersionCommand(Guid ProcessId, Guid VersionId, long ExpectedVersion, string? Note) : ICommand<VersionResponse>;

/// <summary>Starts a new draft from the released version, to change the process.</summary>
public sealed record StartProcessDraftCommand(Guid ProcessId) : ICommand<CreatedResponse>;

public sealed record SetDefaultProcessCommand(Guid MaterialId, Guid? ProcessId) : ICommand<VersionResponse>;

/// <summary>Replaces what the process makes (D-023): all materials, or the listed classes and materials.</summary>
public sealed record SetProcessScopeCommand(
    Guid ProcessId,
    long ExpectedVersion,
    bool AllMaterials,
    IReadOnlyList<Guid> MaterialClassIds,
    IReadOnlyList<Guid> MaterialIds) : ICommand<VersionResponse>;

internal sealed class SetProcessScopeHandler(
    IUnitOfWork unitOfWork,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetProcessScopeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetProcessScopeCommand command, CancellationToken cancellationToken)
    {
        var scope = ProcessScope.Of(command.AllMaterials, command.MaterialClassIds, command.MaterialIds);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var process = await ProcessLocks.ProcessAsync(processes, command.ProcessId, command.ExpectedVersion, cancellationToken);

        var (classes, materials) = await processes.CountExistingAsync(scope.MaterialClassIds, scope.MaterialIds, cancellationToken);
        if (classes != scope.MaterialClassIds.Count || materials != scope.MaterialIds.Count)
        {
            throw new DomainRuleException("The scope names a material class or material that doesn't exist.");
        }

        var envelope = events.Create(EventTypes.ProcessScopeSet, "process", process.Id, new { After = scope });
        var version = await processes.SetScopeAsync(process.Id, scope, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class CreateProcessHandler(
    IUnitOfWork unitOfWork,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateProcessCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateProcessCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.SiteId != Guid.Empty, nameof(command.SiteId), "Required.")
            .RequireText(command.Code, nameof(command.Code), 50)
            .RequireText(command.Name, nameof(command.Name))
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var site = (await processes.GetEquipmentAsync([command.SiteId], cancellationToken)).SingleOrDefault();
        if (site is not { Level: "site" })
        {
            throw new DomainRuleException($"Site {command.SiteId} not found.");
        }

        var process = new Process(ids.NewId(), site.Id, command.Code.Trim(), command.Name.Trim(), Blank(command.Description), IsActive: true);
        var draft = ProcessVersion.FirstDraft(ids.NewId(), process.Id);

        var envelope = events.Create(EventTypes.ProcessCreated, "process", process.Id, new { After = process, DraftVersionId = draft.Id });
        var version = await processes.InsertAsync(process, envelope.OccurredAt, cancellationToken);
        await processes.InsertVersionAsync(draft, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(process.Id, version);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

internal sealed class UpdateProcessHandler(
    IUnitOfWork unitOfWork,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateProcessCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateProcessCommand command, CancellationToken cancellationToken)
    {
        new Validator().RequireText(command.Name, nameof(command.Name)).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ProcessLocks.ProcessAsync(processes, command.Id, command.ExpectedVersion, cancellationToken);

        var updated = current with
        {
            Name = command.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim(),
            IsActive = command.IsActive,
        };
        var envelope = events.Create(EventTypes.ProcessUpdated, "process", updated.Id, new { Before = current, After = updated });
        var version = await processes.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class SaveProcessDraftHandler(
    IUnitOfWork unitOfWork,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SaveProcessDraftCommand, SaveProcessDraftResponse>
{
    public async Task<SaveProcessDraftResponse> HandleAsync(SaveProcessDraftCommand command, CancellationToken cancellationToken)
    {
        var definition = ProcessRules.ToDomain(command.Definition);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var process = await ProcessLocks.ProcessAsync(processes, command.ProcessId, expectedVersion: null, cancellationToken);
        var current = await ProcessLocks.VersionAsync(processes, command.ProcessId, command.VersionId, command.ExpectedVersion, cancellationToken);

        var equipment = await processes.GetEquipmentAsync(ProcessRules.EquipmentIds(definition), cancellationToken);
        if (ProcessRules.EquipmentProblems(definition, process.SiteId, equipment, malformedOnly: true) is { Count: > 0 } malformed)
        {
            throw new DomainRuleException(malformed[0].Message);
        }

        var saved = current.SaveDraft(definition);
        var envelope = events.Create(
            EventTypes.ProcessDraftSaved, "process", process.Id, new { VersionId = saved.Id, saved.Number, After = saved.Definition });
        var version = await processes.UpdateVersionAsync(saved, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var problems = definition.ReleaseProblems()
            .Concat(ProcessRules.EquipmentProblems(definition, process.SiteId, equipment, malformedOnly: false));
        return new SaveProcessDraftResponse(version, [.. problems.Select(ProcessRules.ToDto)]);
    }
}

internal sealed class ReleaseProcessVersionHandler(
    IUnitOfWork unitOfWork,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<ReleaseProcessVersionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ReleaseProcessVersionCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Note is null || command.Note.Length <= 500, nameof(command.Note), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var process = await ProcessLocks.ProcessAsync(processes, command.ProcessId, expectedVersion: null, cancellationToken);
        var draft = await ProcessLocks.VersionAsync(processes, command.ProcessId, command.VersionId, command.ExpectedVersion, cancellationToken);

        var equipment = await processes.GetEquipmentAsync(ProcessRules.EquipmentIds(draft.Definition), cancellationToken);
        if (ProcessRules.EquipmentProblems(draft.Definition, process.SiteId, equipment, malformedOnly: false) is { Count: > 0 } problems)
        {
            throw new DomainRuleException(string.Join(" ", problems.Select(p => p.Message)));
        }

        var at = timeProvider.GetUtcNow();
        var released = draft.Release(at, string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim());
        var previous = await processes.GetReleasedForUpdateAsync(process.Id, cancellationToken);

        var envelope = events.Create(
            EventTypes.ProcessVersionReleased,
            "process",
            process.Id,
            new { VersionId = released.Id, released.Number, ObsoleteVersionId = previous?.Value.Id, released.Definition },
            released.Note);
        if (previous is not null)
        {
            // Obsolete first: only one version per process may be released at a time.
            await processes.UpdateVersionAsync(previous.Value.MakeObsolete(), envelope.OccurredAt, cancellationToken);
        }

        var version = await processes.UpdateVersionAsync(released, envelope.OccurredAt, cancellationToken);
        await processes.InsertReleasedRowsAsync(released, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class StartProcessDraftHandler(
    IUnitOfWork unitOfWork,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<StartProcessDraftCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(StartProcessDraftCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var process = await ProcessLocks.ProcessAsync(processes, command.ProcessId, expectedVersion: null, cancellationToken);
        if (await processes.HasDraftAsync(process.Id, cancellationToken))
        {
            throw new DomainRuleException($"{process.Code} already has a draft; change that one.");
        }

        var released = await processes.GetReleasedForUpdateAsync(process.Id, cancellationToken)
            ?? throw new DomainRuleException($"{process.Code} has no released version to start a draft from.");
        var draft = released.Value.NextDraft(ids.NewId(), ids.NewId);

        var envelope = events.Create(
            EventTypes.ProcessDraftStarted, "process", process.Id, new { VersionId = draft.Id, draft.Number, FromVersionId = released.Value.Id });
        var version = await processes.InsertVersionAsync(draft, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(draft.Id, version);
    }
}

internal sealed class SetDefaultProcessHandler(
    IUnitOfWork unitOfWork,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetDefaultProcessCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetDefaultProcessCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (command.ProcessId is { } processId)
        {
            var process = await ProcessLocks.ProcessAsync(processes, processId, expectedVersion: null, cancellationToken);
            if (!process.IsActive)
            {
                throw new DomainRuleException($"{process.Code} is inactive and can't be a default.");
            }

            if (!await processes.CoversAsync(process.Id, command.MaterialId, cancellationToken))
            {
                throw new DomainRuleException($"{process.Code} doesn't make that material; add it, or its class, to what the process makes first.");
            }
        }

        var envelope = events.Create(
            EventTypes.MaterialDefaultProcessSet, "material", command.MaterialId, new { command.ProcessId });
        var version = await processes.SetMaterialDefaultAsync(command.MaterialId, command.ProcessId, envelope.OccurredAt, cancellationToken)
            ?? throw new NotFoundException($"Material {command.MaterialId} not found.");

        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

/// <summary>Locking order for process commands: the process row, then the version.</summary>
internal static class ProcessLocks
{
    public static async Task<Process> ProcessAsync(
        IProcessRepository processes, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var current = await processes.GetForUpdateAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Process {id} not found.");
        if (expectedVersion is { } expected && current.Version != expected)
        {
            throw new ConflictException($"Process {current.Value.Code} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }

    public static async Task<ProcessVersion> VersionAsync(
        IProcessRepository processes, Guid processId, Guid versionId, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await processes.GetVersionForUpdateAsync(processId, versionId, cancellationToken)
            ?? throw new NotFoundException($"Version {versionId} of process {processId} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"Version {current.Value.Number} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }
}
