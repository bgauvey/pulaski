using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Quality;
using Pulaski.Domain;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Quality;

public sealed record CreateCharacteristicCommand(string Code, string Name, string? Description, string DataKind, Guid? UomId, int Decimals)
    : ICommand<CreatedResponse>;

/// <summary>Changes a characteristic's name, description, unit, decimals and whether it is on; code and kind are fixed.</summary>
public sealed record UpdateCharacteristicCommand(Guid Id, long ExpectedVersion, string Name, string? Description, Guid? UomId, int Decimals, bool IsActive)
    : ICommand<VersionResponse>
{
    /// <summary>A new control chart (D-099); null keeps it. Refused while control limits are in force or to come.</summary>
    public string? ChartType { get; init; }

    public int? SubgroupSize { get; init; }

    /// <summary>The Nelson rules its chart checks (D-099); rule 1 is always added. Null keeps them.</summary>
    public IReadOnlyList<int>? RunRules { get; init; }
}

/// <summary>A specification for the material (#65), starting with an empty draft. A material has at most one.</summary>
public sealed record CreateSpecCommand(Guid MaterialId) : ICommand<CreatedResponse>;

public sealed record SaveSpecDraftCommand(Guid SpecId, Guid VersionId, long ExpectedVersion, SpecDefinitionDto Definition) : ICommand<SaveSpecDraftResponse>;

/// <summary>Releases a complete draft: results are checked against it from now on; the version released before becomes obsolete.</summary>
public sealed record ReleaseSpecVersionCommand(Guid SpecId, Guid VersionId, long ExpectedVersion, string? Note) : ICommand<VersionResponse>;

public sealed record StartSpecDraftCommand(Guid SpecId) : ICommand<CreatedResponse>;

/// <summary>Maps specifications between the API and the domain, and checks what they name.</summary>
internal static class SpecRules
{
    public static SpecDefinition ToDomain(SpecDefinitionDto dto) =>
        new([.. dto.Items.Select(i => new SpecItem(i.Id, i.CharacteristicId, i.Lsl, i.Target, i.Usl, i.MaxDefects, i.HoldOnFail, i.SortOrder, Sampling(i)))]);

    // A plan only when something is asked (#136).
    private static SamplingPlan? Sampling(SpecItemDto i)
    {
        var plan = new SamplingPlan(i.SampleAtStart, i.SampleEveryHours, i.SampleEveryUnits, i.SampleGraceMinutes ?? SamplingPlan.DefaultGrace);
        return plan.IsEmpty ? null : plan;
    }

    public static SpecDefinitionDto ToDto(SpecDefinition definition) => new()
    {
        Items = [.. definition.Items.Select(i => new SpecItemDto
        {
            Id = i.Id,
            CharacteristicId = i.CharacteristicId,
            Lsl = i.Lsl,
            Target = i.Target,
            Usl = i.Usl,
            MaxDefects = i.MaxDefects,
            HoldOnFail = i.HoldOnFail,
            SortOrder = i.SortOrder,
            SampleAtStart = i.Sampling?.AtStart ?? false,
            SampleEveryHours = i.Sampling?.EveryHours,
            SampleEveryUnits = i.Sampling?.EveryUnits,
            SampleGraceMinutes = i.Sampling?.GraceMinutes,
        })],
    };

    public static IReadOnlyList<Guid> CharacteristicIds(SpecDefinition definition) => [.. definition.Items.Select(i => i.CharacteristicId).Distinct()];

    /// <summary>
    /// Each item must name a real characteristic and fit its kind; <paramref name="malformedOnly"/> leaves out what a draft may still have
    /// (a characteristic switched off, limits not filled in yet).
    /// </summary>
    public static IReadOnlyList<SpecProblem> ReferenceProblems(SpecDefinition definition, IReadOnlyList<Characteristic> characteristics, bool malformedOnly)
    {
        var byId = characteristics.ToDictionary(c => c.Id);
        var problems = new List<SpecProblem>();
        foreach (var item in definition.Items)
        {
            if (!byId.TryGetValue(item.CharacteristicId, out var characteristic))
            {
                problems.Add(new("unknown_characteristic", "An item names a characteristic that doesn't exist.", item.Id));
                continue;
            }

            if (!malformedOnly && !characteristic.IsActive)
            {
                problems.Add(new("inactive_characteristic", $"{characteristic.Code} is switched off.", item.Id));
            }

            var kind = SpecDefinition.KindProblems(item, characteristic);
            problems.AddRange(malformedOnly ? kind.Where(p => p.Code == "kind_mismatch") : kind);
        }

        return problems;
    }

    public static SpecProblemDto ToDto(SpecProblem problem) =>
        new(problem.Code, problem.Message, problem.ItemId) { Template = problem.Text.Template, Values = problem.Text.Values };

    public static Characteristic Validate(Characteristic characteristic)
    {
        new Validator()
            .Require(nameof(characteristic.Code), Characteristic.CodeProblem(characteristic.Code))
            .Require(characteristic.Name.Length is > 0 and <= Characteristic.MaxName, nameof(characteristic.Name), $"1 to {Characteristic.MaxName} characters.")
            .Require(characteristic.Description is null || characteristic.Description.Length <= Characteristic.MaxDescription, nameof(characteristic.Description),
                $"At most {Characteristic.MaxDescription} characters.")
            .Require(CharacteristicKinds.All.Contains(characteristic.DataKind), nameof(characteristic.DataKind), "variable or attribute.")
            .Require(characteristic.Decimals is >= 0 and <= 6, nameof(characteristic.Decimals), "0 to 6.")
            .Require(characteristic.UomId is null || characteristic.DataKind == CharacteristicKinds.Variable, nameof(characteristic.UomId),
                "Only a measured characteristic has a unit.")
            .Require(nameof(characteristic.ChartType), Domain.Quality.Spc.ChartTypes.Problem(characteristic.ChartType, characteristic.DataKind, characteristic.SubgroupSize))
            .ThrowIfInvalid();
        return characteristic;
    }

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class CreateCharacteristicHandler(IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateCharacteristicCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateCharacteristicCommand command, CancellationToken cancellationToken)
    {
        var characteristic = SpecRules.Validate(new Characteristic(
            ids.NewId(), (command.Code ?? "").Trim().ToUpperInvariant(), (command.Name ?? "").Trim(), SpecRules.Text(command.Description),
            command.DataKind?.Trim() ?? "", command.UomId, command.Decimals, IsActive: true));

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (await quality.CharacteristicCodeExistsAsync(characteristic.Code, cancellationToken))
        {
            throw new ConflictException($"There is already a characteristic {characteristic.Code}.");
        }

        var envelope = events.Create(EventTypes.CharacteristicCreated, "quality_characteristic", characteristic.Id, new { After = characteristic });
        var version = await quality.InsertCharacteristicAsync(characteristic, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(characteristic.Id, version);
    }
}

internal sealed class UpdateCharacteristicHandler(IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<UpdateCharacteristicCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateCharacteristicCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await quality.GetCharacteristicForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Characteristic {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var after = SpecRules.Validate(current.Value with
        {
            Name = (command.Name ?? "").Trim(),
            Description = SpecRules.Text(command.Description),
            UomId = command.UomId,
            Decimals = command.Decimals,
            IsActive = command.IsActive,
            ChartType = command.ChartType?.Trim() ?? current.Value.ChartType,
            SubgroupSize = command.SubgroupSize ?? current.Value.SubgroupSize,
            RunRules = command.RunRules is { } rules ? Domain.Quality.Spc.RunRules.Normalize(rules) : current.Value.RunRules,
        });
        if ((after.ChartType != current.Value.ChartType || after.SubgroupSize != current.Value.SubgroupSize)
            && await quality.HasOpenLimitsAsync(after.Id, timeProvider.GetUtcNow(), cancellationToken))
        {
            throw new DomainRuleException($"{after.Code} has control limits in force; end them before changing its chart, since they were worked out for this one.");
        }

        var envelope = events.Create(EventTypes.CharacteristicUpdated, "quality_characteristic", after.Id, new { Before = current.Value, After = after });
        var version = await quality.UpdateCharacteristicAsync(after, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class CreateSpecHandler(IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateSpecCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateSpecCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(command.MaterialId != Guid.Empty, nameof(command.MaterialId), "Required.").ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var material = await quality.GetMaterialAsync(command.MaterialId, cancellationToken)
            ?? throw new DomainRuleException($"Material {command.MaterialId} not found.");
        if (await quality.SpecExistsForMaterialAsync(command.MaterialId, cancellationToken))
        {
            throw new ConflictException($"{material.Code} already has a specification; change that one.");
        }

        var spec = new MaterialSpec(ids.NewId(), command.MaterialId, IsActive: true);
        var draft = SpecVersion.FirstDraft(ids.NewId(), spec.Id);
        var envelope = events.Create(EventTypes.SpecCreated, "material_spec", spec.Id, new { After = spec, MaterialCode = material.Code, DraftVersionId = draft.Id });
        var version = await quality.InsertSpecAsync(spec, envelope.OccurredAt, cancellationToken);
        await quality.InsertVersionAsync(draft, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(spec.Id, version);
    }
}

internal sealed class SaveSpecDraftHandler(
    IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, Approvals.IReleaseApprovalRepository? approvals = null)
    : ICommandHandler<SaveSpecDraftCommand, SaveSpecDraftResponse>
{
    public async Task<SaveSpecDraftResponse> HandleAsync(SaveSpecDraftCommand command, CancellationToken cancellationToken)
    {
        var definition = SpecRules.ToDomain(command.Definition);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var spec = await SpecLocks.SpecAsync(quality, command.SpecId, cancellationToken);
        var current = await SpecLocks.VersionAsync(quality, spec.Id, command.VersionId, command.ExpectedVersion, cancellationToken);
        await Approvals.ReleaseGate.EnsureEditableAsync(approvals, current.Id, cancellationToken);
        var characteristics = await quality.GetCharacteristicsAsync(SpecRules.CharacteristicIds(definition), cancellationToken);
        if (SpecRules.ReferenceProblems(definition, characteristics, malformedOnly: true) is { Count: > 0 } malformed)
        {
            throw new DomainRuleException(malformed[0].Text);
        }

        var saved = current.SaveDraft(definition);
        var envelope = events.Create(EventTypes.SpecDraftSaved, "material_spec", spec.Id, new { VersionId = saved.Id, saved.Number, After = saved.Definition });
        var version = await quality.UpdateVersionAsync(saved, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var problems = definition.ReleaseProblems().Concat(SpecRules.ReferenceProblems(definition, characteristics, malformedOnly: false));
        return new SaveSpecDraftResponse(version, [.. problems.Select(SpecRules.ToDto)]);
    }
}

internal sealed class ReleaseSpecVersionHandler(
    IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider,
    Approvals.IReleaseApprovalRepository? approvals = null)
    : ICommandHandler<ReleaseSpecVersionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ReleaseSpecVersionCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Note is null || command.Note.Length <= 500, nameof(command.Note), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var spec = await SpecLocks.SpecAsync(quality, command.SpecId, cancellationToken);
        var draft = await SpecLocks.VersionAsync(quality, spec.Id, command.VersionId, command.ExpectedVersion, cancellationToken);

        // Where a site that makes the material asks for approval (#138), the draft is submitted instead.
        if (approvals is not null)
        {
            var (sites, _) = await approvals.MaterialSitesAsync(spec.MaterialId, null, cancellationToken);
            await Approvals.ReleaseGate.EnsureDirectReleaseAsync(approvals, Domain.Approvals.ReleaseKinds.Spec, draft.Id, sites, cancellationToken);
        }

        var version = await new SpecReleaser(quality, eventLog, events, timeProvider).ReleaseAsync(spec, draft, command.Note, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

/// <summary>Releasing a locked specification draft (D-085), in the caller's transaction: by its own command, or by an approval (#138).</summary>
internal sealed class SpecReleaser(IQualityRepository quality, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
{
    /// <exception cref="DomainRuleException">The draft can't be released as it is.</exception>
    public async Task EnsureReleasableAsync(SpecVersion draft, CancellationToken cancellationToken)
    {
        var characteristics = await quality.GetCharacteristicsAsync(SpecRules.CharacteristicIds(draft.Definition), cancellationToken);
        var problems = draft.Definition.ReleaseProblems().Concat(SpecRules.ReferenceProblems(draft.Definition, characteristics, malformedOnly: false)).ToList();
        if (problems.Count > 0)
        {
            throw new DomainRuleException(UserMessage.Join(problems.Select(p => p.Text)));
        }
    }

    public async Task<long> ReleaseAsync(MaterialSpec spec, SpecVersion draft, string? note, CancellationToken cancellationToken)
    {
        await EnsureReleasableAsync(draft, cancellationToken);
        var previous = await quality.GetReleasedForUpdateAsync(spec.Id, cancellationToken);
        var envelope = events.Create(
            EventTypes.SpecVersionReleased, "material_spec", spec.Id,
            new { VersionId = draft.Id, draft.Number, ObsoleteVersionId = previous?.Value.Id, draft.Definition },
            SpecRules.Text(note));
        var released = draft.Release(timeProvider.GetUtcNow(), envelope.ActorId, SpecRules.Text(note));
        if (previous is not null)
        {
            // Obsolete first: only one version of a specification may be released at a time.
            await quality.UpdateVersionAsync(previous.Value.MakeObsolete(), envelope.OccurredAt, cancellationToken);
        }

        var version = await quality.UpdateVersionAsync(released, envelope.OccurredAt, cancellationToken);
        await quality.InsertReleasedItemsAsync(released, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return version;
    }
}

/// <summary>A specification as release approval sees it (#138b): the sites that make its material decide.</summary>
internal sealed class SpecReleasable(IQualityRepository quality, Approvals.IReleaseApprovalRepository approvals, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : Approvals.IReleasableDocument
{
    private MaterialSpec? _spec;
    private SpecVersion? _draft;

    public string Kind => Domain.Approvals.ReleaseKinds.Spec;

    public IReadOnlyList<string> DraftSavedEvents { get; } = [EventTypes.SpecDraftSaved];

    public async Task<Approvals.ReleasableDraft> LockAsync(Guid documentId, Guid versionId, long? expectedVersion, CancellationToken cancellationToken)
    {
        _spec = await SpecLocks.SpecAsync(quality, documentId, cancellationToken);
        var current = await quality.GetVersionForUpdateAsync(documentId, versionId, cancellationToken)
            ?? throw new NotFoundException($"Version {versionId} of specification {documentId} not found.");
        if (expectedVersion is { } expected && current.Version != expected)
        {
            throw new ConflictException($"Version {current.Value.Number} was changed by someone else. Reload and try again.");
        }

        _draft = current.Value;
        var (sites, code) = await approvals.MaterialSitesAsync(_spec.MaterialId, null, cancellationToken);
        return new Approvals.ReleasableDraft(_spec.Id, _draft.Id, code, _draft.Number, sites);
    }

    public Task EnsureReleasableAsync(Approvals.ReleasableDraft draft, CancellationToken cancellationToken) =>
        _draft!.Status != SpecVersionStatus.Draft
            ? throw new DomainRuleException($"Version {_draft.Number} of the specification for {draft.Code} isn't a draft.")
            : new SpecReleaser(quality, eventLog, events, timeProvider).EnsureReleasableAsync(_draft, cancellationToken);

    public async Task<IReadOnlyList<MessageDto>> ReleaseAsync(Approvals.ReleasableDraft draft, string? note, CancellationToken cancellationToken)
    {
        await new SpecReleaser(quality, eventLog, events, timeProvider).ReleaseAsync(_spec!, _draft!, note, cancellationToken);
        return [];
    }
}

internal sealed class StartSpecDraftHandler(IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<StartSpecDraftCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(StartSpecDraftCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var spec = await SpecLocks.SpecAsync(quality, command.SpecId, cancellationToken);
        if (await quality.HasDraftAsync(spec.Id, cancellationToken))
        {
            throw new DomainRuleException("This specification already has a draft; change that one.");
        }

        var released = await quality.GetReleasedForUpdateAsync(spec.Id, cancellationToken)
            ?? throw new DomainRuleException("This specification has no released version to start a draft from.");
        var draft = released.Value.NextDraft(ids.NewId(), ids.NewId);
        var envelope = events.Create(EventTypes.SpecDraftStarted, "material_spec", spec.Id, new { VersionId = draft.Id, draft.Number, FromVersionId = released.Value.Id });
        var version = await quality.InsertVersionAsync(draft, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(draft.Id, version);
    }
}

/// <summary>Locking order for specification commands: the specification row, then the version.</summary>
internal static class SpecLocks
{
    public static async Task<MaterialSpec> SpecAsync(IQualityRepository quality, Guid id, CancellationToken cancellationToken) =>
        (await quality.GetSpecForUpdateAsync(id, cancellationToken))?.Value ?? throw new NotFoundException($"Specification {id} not found.");

    public static async Task<SpecVersion> VersionAsync(IQualityRepository quality, Guid specId, Guid versionId, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await quality.GetVersionForUpdateAsync(specId, versionId, cancellationToken)
            ?? throw new NotFoundException($"Version {versionId} of specification {specId} not found.");
        return current.Version == expectedVersion
            ? current.Value
            : throw new ConflictException($"Version {current.Value.Number} was changed by someone else. Reload and try again.");
    }
}
