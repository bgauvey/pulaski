using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Application.Units;
using Pulaski.Contracts.Boms;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Boms;

/// <summary>A BOM for the material, for one process or (null) for any (D-025). Starts with an empty draft.</summary>
public sealed record CreateBomCommand(Guid MaterialId, Guid? ProcessId) : ICommand<CreatedResponse>;

/// <summary>Replaces a draft's base quantity, unit and items.</summary>
public sealed record SaveBomDraftCommand(Guid BomId, Guid VersionId, long ExpectedVersion, BomDefinitionDto Definition)
    : ICommand<SaveBomDraftResponse>;

/// <summary>Releases a complete draft for orders; the version released before it becomes obsolete.</summary>
public sealed record ReleaseBomVersionCommand(Guid BomId, Guid VersionId, long ExpectedVersion, string? Note) : ICommand<VersionResponse>;

/// <summary>Starts a new draft from the released version, to change the BOM.</summary>
public sealed record StartBomDraftCommand(Guid BomId) : ICommand<CreatedResponse>;

internal sealed class CreateBomHandler(
    IUnitOfWork unitOfWork,
    IBomRepository boms,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateBomCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateBomCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(command.MaterialId != Guid.Empty, nameof(command.MaterialId), "Required.").ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var material = (await boms.GetMaterialsAsync([command.MaterialId], cancellationToken)).SingleOrDefault()
            ?? throw new DomainRuleException($"Material {command.MaterialId} not found.");

        if (command.ProcessId is { } processId)
        {
            var process = (await processes.GetForUpdateAsync(processId, cancellationToken))?.Value
                ?? throw new DomainRuleException($"Process {processId} not found.");
            if (!await processes.CoversAsync(process.Id, material.Id, cancellationToken))
            {
                throw new DomainRuleException($"{process.Code} doesn't make {material.Code}; add it, or its class, to what the process makes first.");
            }
        }

        var bom = new Bom(ids.NewId(), material.Id, command.ProcessId, IsActive: true);
        var draft = BomVersion.FirstDraft(ids.NewId(), bom.Id, material.BaseUomId);

        var envelope = events.Create(EventTypes.BomCreated, "bom", bom.Id, new { After = bom, DraftVersionId = draft.Id });
        var version = await boms.InsertAsync(bom, envelope.OccurredAt, cancellationToken);
        await boms.InsertVersionAsync(draft, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(bom.Id, version);
    }
}

internal sealed class SaveBomDraftHandler(
    IUnitOfWork unitOfWork,
    IBomRepository boms,
    IProcessRepository processes,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SaveBomDraftCommand, SaveBomDraftResponse>
{
    public async Task<SaveBomDraftResponse> HandleAsync(SaveBomDraftCommand command, CancellationToken cancellationToken)
    {
        var definition = BomRules.ToDomain(command.Definition);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var bom = await BomLocks.BomAsync(boms, command.BomId, cancellationToken);
        var current = await BomLocks.VersionAsync(boms, command.BomId, command.VersionId, command.ExpectedVersion, cancellationToken);

        var materials = await boms.GetMaterialsAsync(BomRules.MaterialIds(definition, bom.MaterialId), cancellationToken);
        if (BomRules.ReferenceProblems(definition, bom, materials, null, malformedOnly: true) is { Count: > 0 } malformed)
        {
            throw new DomainRuleException(malformed[0].Message);
        }

        var saved = current.SaveDraft(definition);
        var envelope = events.Create(EventTypes.BomDraftSaved, "bom", bom.Id, new { VersionId = saved.Id, saved.Number, After = saved.Definition });
        var version = await boms.UpdateVersionAsync(saved, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        var operations = await BomLocks.OperationsAsync(processes, bom, cancellationToken);
        var converterFor = await BomLocks.ConvertersAsync(units, materials, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var problems = definition.ReleaseProblems()
            .Concat(BomRules.ReferenceProblems(definition, bom, materials, operations, malformedOnly: false, converterFor));
        return new SaveBomDraftResponse(version, [.. problems.Select(BomRules.ToDto)]);
    }
}

internal sealed class ReleaseBomVersionHandler(
    IUnitOfWork unitOfWork,
    IBomRepository boms,
    IProcessRepository processes,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<ReleaseBomVersionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ReleaseBomVersionCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Note is null || command.Note.Length <= 500, nameof(command.Note), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var bom = await BomLocks.BomAsync(boms, command.BomId, cancellationToken);
        var draft = await BomLocks.VersionAsync(boms, command.BomId, command.VersionId, command.ExpectedVersion, cancellationToken);

        var materials = await boms.GetMaterialsAsync(BomRules.MaterialIds(draft.Definition, bom.MaterialId), cancellationToken);
        var operations = await BomLocks.OperationsAsync(processes, bom, cancellationToken);
        var converterFor = await BomLocks.ConvertersAsync(units, materials, cancellationToken);
        if (BomRules.ReferenceProblems(draft.Definition, bom, materials, operations, malformedOnly: false, converterFor) is { Count: > 0 } problems)
        {
            throw new DomainRuleException(string.Join(" ", problems.Select(p => p.Message)));
        }

        var released = draft.Release(timeProvider.GetUtcNow(), string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim());
        var previous = await boms.GetReleasedForUpdateAsync(bom.Id, cancellationToken);

        var envelope = events.Create(
            EventTypes.BomVersionReleased,
            "bom",
            bom.Id,
            new { VersionId = released.Id, released.Number, ObsoleteVersionId = previous?.Value.Id, released.Definition },
            released.Note);
        if (previous is not null)
        {
            // Obsolete first: only one version per BOM may be released at a time.
            await boms.UpdateVersionAsync(previous.Value.MakeObsolete(), envelope.OccurredAt, cancellationToken);
        }

        var version = await boms.UpdateVersionAsync(released, envelope.OccurredAt, cancellationToken);
        await boms.InsertReleasedRowsAsync(released, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class StartBomDraftHandler(
    IUnitOfWork unitOfWork,
    IBomRepository boms,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<StartBomDraftCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(StartBomDraftCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var bom = await BomLocks.BomAsync(boms, command.BomId, cancellationToken);
        if (await boms.HasDraftAsync(bom.Id, cancellationToken))
        {
            throw new DomainRuleException("This BOM already has a draft; change that one.");
        }

        var released = await boms.GetReleasedForUpdateAsync(bom.Id, cancellationToken)
            ?? throw new DomainRuleException("This BOM has no released version to start a draft from.");
        var draft = released.Value.NextDraft(ids.NewId(), ids.NewId);

        var envelope = events.Create(
            EventTypes.BomDraftStarted, "bom", bom.Id, new { VersionId = draft.Id, draft.Number, FromVersionId = released.Value.Id });
        var version = await boms.InsertVersionAsync(draft, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(draft.Id, version);
    }
}

/// <summary>Locking order for BOM commands: the BOM row, then the version.</summary>
internal static class BomLocks
{
    public static async Task<Bom> BomAsync(IBomRepository boms, Guid id, CancellationToken cancellationToken) =>
        (await boms.GetForUpdateAsync(id, cancellationToken))?.Value ?? throw new NotFoundException($"BOM {id} not found.");

    public static async Task<BomVersion> VersionAsync(
        IBomRepository boms, Guid bomId, Guid versionId, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await boms.GetVersionForUpdateAsync(bomId, versionId, cancellationToken)
            ?? throw new NotFoundException($"Version {versionId} of BOM {bomId} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"Version {current.Value.Number} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }

    /// <summary>A converter per material for the BOM's own material and its components (D-026).</summary>
    public static async Task<Func<Guid, UnitConverter>> ConvertersAsync(
        IUnitRepository units, IReadOnlyList<BomMaterialRow> materials, CancellationToken cancellationToken)
    {
        var all = await units.GetUnitsAsync(cancellationToken);
        var conversions = await units.GetConversionsAsync([.. materials.Select(m => m.Id)], cancellationToken);
        return materialId => UnitConversions.For(materialId, all, conversions);
    }

    /// <summary>The operation numbers of the BOM's process's released version; null when the BOM has no process or the process no released version.</summary>
    public static async Task<IReadOnlySet<int>?> OperationsAsync(IProcessRepository processes, Bom bom, CancellationToken cancellationToken) =>
        bom.ProcessId is { } processId && await processes.GetReleasedAsync(processId, lockVersion: false, cancellationToken) is { } route
            ? route.Version.Definition.Operations.Select(o => o.Number).ToHashSet()
            : null;
}
