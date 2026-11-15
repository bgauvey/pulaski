using Microsoft.Extensions.Logging;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Quality;
using Pulaski.Domain;
using Pulaski.Domain.Quality;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Quality;

/// <summary>A characteristic's tag bindings (D-099).</summary>
public sealed record ListTagBindingsQuery(Guid CharacteristicId) : IQuery<IReadOnlyList<TagBindingDto>>;

/// <summary>Process-value tags that can be bound.</summary>
public sealed record ListProcessValueTagsQuery : IQuery<IReadOnlyList<ProcessValueTagDto>>;

/// <summary>Samples a measured characteristic from a process-value tag on its line every so many minutes, from now on.</summary>
public sealed record AddTagBindingCommand(Guid CharacteristicId, Guid EquipmentTagId, int IntervalMinutes) : ICommand<CreatedResponse>;

/// <summary>Changes a binding's interval, or switches it off or on again (from now on; missed intervals aren't made up).</summary>
public sealed record UpdateTagBindingCommand(Guid Id, long ExpectedVersion, int IntervalMinutes, bool IsActive) : ICommand<VersionResponse>;

internal sealed class ListTagBindingsHandler(IQualityReadStore store) : IQueryHandler<ListTagBindingsQuery, IReadOnlyList<TagBindingDto>>
{
    public Task<IReadOnlyList<TagBindingDto>> HandleAsync(ListTagBindingsQuery query, CancellationToken cancellationToken) =>
        store.ListBindingsAsync(query.CharacteristicId, cancellationToken);
}

internal sealed class ListProcessValueTagsHandler(IQualityReadStore store) : IQueryHandler<ListProcessValueTagsQuery, IReadOnlyList<ProcessValueTagDto>>
{
    public Task<IReadOnlyList<ProcessValueTagDto>> HandleAsync(ListProcessValueTagsQuery query, CancellationToken cancellationToken) =>
        store.ListProcessValueTagsAsync(cancellationToken);
}

internal sealed class AddTagBindingHandler(IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<AddTagBindingCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(AddTagBindingCommand command, CancellationToken cancellationToken)
    {
        var interval = AutoSampling.EnsureInterval(command.IntervalMinutes);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var characteristic = (await quality.GetCharacteristicForUpdateAsync(command.CharacteristicId, cancellationToken)
            ?? throw new NotFoundException($"Characteristic {command.CharacteristicId} not found.")).Value;
        if (characteristic.DataKind != CharacteristicKinds.Variable || !characteristic.IsActive)
        {
            throw new DomainRuleException($"{characteristic.Code} {(characteristic.IsActive ? "is inspected; only a measured characteristic can be sampled from a tag" : "is switched off")}.");
        }

        var tag = await quality.GetBindableTagAsync(command.EquipmentTagId, cancellationToken) ?? throw new NotFoundException($"Tag {command.EquipmentTagId} not found.");
        if (tag.Role != "process_value" || !tag.IsActive)
        {
            throw new DomainRuleException("Choose an active process-value tag.");
        }

        if (tag.LineId is not { } lineId)
        {
            throw new DomainRuleException("The tag's equipment isn't on a line that runs jobs, so its results would have no job or material.");
        }

        if (await quality.HasActiveBindingAsync(characteristic.Id, lineId, cancellationToken))
        {
            throw new ConflictException($"{characteristic.Code} is already sampled on {tag.LineCode}; switch that off first.");
        }

        var now = timeProvider.GetUtcNow();
        var binding = new TagBinding(ids.NewId(), characteristic.Id, tag.TagId, lineId, tag.SiteId, interval, true, now);
        var envelope = events.Create(EventTypes.CharacteristicTagBound, "quality_characteristic", characteristic.Id, new { After = binding });
        var version = await quality.InsertBindingAsync(binding, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(binding.Id, version);
    }
}

internal sealed class UpdateTagBindingHandler(IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<UpdateTagBindingCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateTagBindingCommand command, CancellationToken cancellationToken)
    {
        var interval = AutoSampling.EnsureInterval(command.IntervalMinutes);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await quality.GetBindingForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Binding {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("The binding was changed by someone else. Reload and try again.");
        }

        var before = current.Value;
        if (command.IsActive && before.EquipmentTagId is null)
        {
            throw new DomainRuleException("Its tag was deleted; bind another tag instead.");
        }

        if (command.IsActive && !before.IsActive && await quality.HasActiveBindingAsync(before.CharacteristicId, before.LineId, cancellationToken))
        {
            throw new ConflictException("The characteristic is already sampled on that line; switch that off first.");
        }

        // Switched back on, it starts from now: missed intervals aren't made up.
        var now = timeProvider.GetUtcNow();
        var after = before with
        {
            IntervalMinutes = interval,
            IsActive = command.IsActive,
            SampledThrough = command.IsActive && !before.IsActive ? now : before.SampledThrough,
        };
        var envelope = events.Create(EventTypes.CharacteristicTagChanged, "quality_characteristic", before.CharacteristicId, new { Before = before, After = after });
        var version = await quality.UpdateBindingAsync(after, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

/// <summary>
/// The automatic sampling of process values (D-099), run by a background service: each active binding, locked (so two rounds never take the
/// same), makes a result (source <c>auto</c>) at each interval end since it last did, from the value held at consecutive minutes, for the job
/// running on its line then, through the same recorder as people use (specification check, run rules, alerts). An interval whose readings are bad
/// or stale, or with no job running, is skipped. One transaction per binding a round.
/// </summary>
public sealed class TagSampler(
    IUnitOfWork unitOfWork,
    IQualityRepository quality,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    IRequestContext request,
    TimeProvider time,
    ILogger<TagSampler>? logger = null)
{
    /// <summary>The most interval ends a binding takes in one round; the rest wait for the next.</summary>
    public const int MaxTicksPerRound = 96;

    /// <returns>The results made.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> bindings;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            bindings = await quality.ListActiveBindingsAsync(cancellationToken);
        }

        var made = 0;
        foreach (var id in bindings)
        {
            try
            {
                made += await SampleAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is DomainRuleException or ConflictException or NotFoundException)
            {
                logger?.LogWarning(ex, "Couldn't sample binding {BindingId}.", id);
            }
        }

        return made;
    }

    private async Task<int> SampleAsync(Guid bindingId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (await quality.LockBindingAsync(bindingId, cancellationToken) is not { EquipmentTagId: { } tagId } binding)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var ticks = AutoSampling.Ticks(binding.SampledThrough, now, binding.IntervalMinutes, MaxTicksPerRound);
        if (ticks.Count == 0)
        {
            return 0;
        }

        var characteristic = (await quality.GetCharacteristicsAsync([binding.CharacteristicId], cancellationToken)).Single();
        var made = 0;
        if (characteristic.IsActive && characteristic.DataKind == CharacteristicKinds.Variable)
        {
            var n = characteristic.ChartType is ChartTypes.XbarR or ChartTypes.XbarS ? characteristic.SubgroupSize : 1;
            var readings = await quality.ListReadingsAsync(tagId, ticks[0] - AutoSampling.Spacing * (n - 1) - AutoSampling.MaxAge, ticks[^1], cancellationToken);
            var recorder = new QualityRecorder(quality, inventory, eventLog, events, ids, request);
            foreach (var tick in ticks)
            {
                if (AutoSampling.Subgroup(readings, tick, n) is not { } values
                    || await quality.FindRunningJobAsync(binding.LineId, tick, cancellationToken) is not { } jobId
                    || await quality.GetJobContextAsync(jobId, tick, cancellationToken) is not { MaterialId: not null } job)
                {
                    continue;
                }

                await recorder.RecordAsync(
                    binding.SiteId, characteristic, null, job, QualityMeasurement.Measured([.. values.Select(v => (decimal)Math.Round(v, 6))]), tick,
                    QualitySources.Auto, null, cancellationToken);
                made++;
            }
        }

        await quality.AdvanceBindingAsync(binding.Id, ticks[^1], now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return made;
    }
}
