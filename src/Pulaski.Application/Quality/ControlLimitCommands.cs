using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Quality;
using Pulaski.Domain;
using Pulaski.Domain.Quality;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Quality;

/// <summary>A characteristic's control limits, every period (D-099).</summary>
public sealed record ListControlLimitsQuery(Guid CharacteristicId) : IQuery<IReadOnlyList<ControlLimitDto>>;

/// <summary>What limits a baseline gives, with each of its results, for review; saves nothing.</summary>
public sealed record GetLimitBaselineQuery(
    Guid CharacteristicId, Guid MaterialId, Guid? EquipmentId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<Guid> LeftOutSampleIds) : IQuery<LimitBaselineDto>;

/// <summary>
/// Approves the limits a baseline gives (D-099). They are worked out again under the characteristic's lock, from the same results less those
/// left out; different limits from those reviewed are refused (409). An open period for the same scope ends where these start.
/// </summary>
public sealed record ApproveControlLimitsCommand(
    Guid CharacteristicId, Guid MaterialId, Guid? EquipmentId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<Guid> LeftOutSampleIds,
    double CenterLine, double Ucl, double Lcl, DateTimeOffset? EffectiveFrom, string? Note) : ICommand<ControlLimitChangeResponse>;

/// <summary>Limits entered by hand, with a reason; placed as approved ones are.</summary>
public sealed record SetManualControlLimitsCommand(
    Guid CharacteristicId, Guid MaterialId, Guid? EquipmentId, double CenterLine, double Ucl, double Lcl, double? DispersionCenter, double? DispersionUcl,
    double? DispersionLcl, DateTimeOffset? EffectiveFrom, string Note) : ICommand<ControlLimitChangeResponse>;

/// <summary>Ends a period of limits now; the chart has none for its scope until new ones are approved.</summary>
public sealed record EndControlLimitsCommand(Guid LimitId, string? Note) : ICommand<ControlLimitChangeResponse>;

internal static class ControlLimitRules
{
    /// <summary>The longest baseline window.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(366);

    public static void ValidateWindow(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now) =>
        new Validator()
            .Require(from < to, "To", "The baseline ends after it starts.")
            .Require(to - from <= MaxWindow, "From", "A baseline covers at most 366 days.")
            .Require(from <= now, "From", "The baseline starts in the past.")
            .ThrowIfInvalid();

    /// <summary>The characteristic, locked: limit changes for it serialize, and its chart can't change meanwhile.</summary>
    public static async Task<Characteristic> LockCharacteristicAsync(IQualityRepository quality, Guid id, CancellationToken cancellationToken)
    {
        var characteristic = (await quality.GetCharacteristicForUpdateAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Characteristic {id} not found.")).Value;
        return characteristic.IsActive ? characteristic : throw new DomainRuleException($"{characteristic.Code} is switched off.");
    }

    /// <summary>The material and line must exist; a line runs jobs. Returns the material's and line's codes.</summary>
    public static async Task<(string Material, string? Line)> ScopeAsync(IQualityRepository quality, Guid materialId, Guid? equipmentId, CancellationToken cancellationToken)
    {
        var material = await quality.GetMaterialAsync(materialId, cancellationToken) ?? throw new DomainRuleException($"Material {materialId} not found.");
        string? line = null;
        if (equipmentId is { } id)
        {
            line = await quality.GetLineCodeAsync(id, cancellationToken) ?? throw new DomainRuleException("Limits for one line name equipment that runs jobs.");
        }

        return (material.Code, line);
    }

    /// <summary>The baseline's subgroups less those left out, with the limits they give for the characteristic's chart.</summary>
    public static LimitResult Work(Characteristic characteristic, IReadOnlyList<SpcSubgroup> subgroups, IReadOnlyCollection<Guid> leftOut) =>
        ControlLimits.Compute(characteristic.ChartType, [.. subgroups.Where(s => !leftOut.Contains(s.SampleId))], characteristic.SubgroupSize);

    /// <summary>Starts now or later.</summary>
    public static DateTimeOffset Start(DateTimeOffset? requested, DateTimeOffset now)
    {
        var from = requested ?? now;
        return from < now.AddMinutes(-1) ? throw new DomainRuleException("Limits start now or later; history keeps the limits it was charted with.") : from;
    }

    /// <summary>Places the new limits, ends the period it follows, and records it.</summary>
    public static async Task<ControlLimitChangeResponse> SaveAsync(
        IQualityRepository quality, IEventLog eventLog, IEventFactory events, ControlLimit limit, string what, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await quality.GetLimitsAsync(limit.CharacteristicId, limit.MaterialId, limit.EquipmentId, cancellationToken);
        var ended = ControlLimitPeriods.Place(limit, existing);
        foreach (var e in ended)
        {
            await quality.EndLimitAsync(e.Id, e.EffectiveTo!.Value, limit.ApprovedBy, now, cancellationToken);
        }

        await quality.InsertLimitAsync(limit, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.ControlLimitsApproved, "control_limit", limit.Id, new
        {
            What = what,
            After = limit,
            Ended = ended.Select(e => e.Id),
        }), cancellationToken);
        return new ControlLimitChangeResponse(limit.Id, [.. ended.Select(e => e.Id)]);
    }
}

internal sealed class ListControlLimitsHandler(IQualityReadStore store, TimeProvider timeProvider) : IQueryHandler<ListControlLimitsQuery, IReadOnlyList<ControlLimitDto>>
{
    public Task<IReadOnlyList<ControlLimitDto>> HandleAsync(ListControlLimitsQuery query, CancellationToken cancellationToken) =>
        store.ListLimitsAsync(query.CharacteristicId, timeProvider.GetUtcNow(), cancellationToken);
}

internal sealed class GetLimitBaselineHandler(IQualityReadStore store, TimeProvider timeProvider) : IQueryHandler<GetLimitBaselineQuery, LimitBaselineDto>
{
    public async Task<LimitBaselineDto> HandleAsync(GetLimitBaselineQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        ControlLimitRules.ValidateWindow(query.From, query.To, now);
        var characteristic = await store.GetCharacteristicsAsync([query.CharacteristicId], cancellationToken) is [var found, ..]
            ? found
            : throw new NotFoundException($"Characteristic {query.CharacteristicId} not found.");
        var subgroups = await store.ListBaselineAsync(query.CharacteristicId, query.MaterialId, query.EquipmentId, query.From, query.To, cancellationToken);
        var leftOut = query.LeftOutSampleIds.ToHashSet();
        var result = ControlLimitRules.Work(characteristic, subgroups, leftOut);
        var unusable = result.LeftOut.ToDictionary(l => l.Subgroup.SampleId, l => l.Reason);
        var points = ControlLimits.Points(characteristic.ChartType, subgroups).ToDictionary(p => p.SampleId);
        var current = (await store.ListLimitsAsync(query.CharacteristicId, now, cancellationToken))
            .FirstOrDefault(l => l.IsInEffect && l.MaterialId == query.MaterialId && l.EquipmentId == query.EquipmentId);
        var limits = result.Limits;

        return new LimitBaselineDto
        {
            CharacteristicId = characteristic.Id,
            ChartType = characteristic.ChartType,
            SubgroupSize = characteristic.SubgroupSize,
            From = query.From,
            To = query.To,
            Subgroups = result.Subgroups,
            MinSubgroups = ControlLimits.MinSubgroups,
            CenterLine = limits?.Center,
            Ucl = limits?.Ucl,
            Lcl = limits?.Lcl,
            DispersionCenter = limits?.DispersionCenter,
            DispersionUcl = limits?.DispersionUcl,
            DispersionLcl = limits?.DispersionLcl,
            NotEnough = result.NotEnough,
            Warnings = result.Warnings,
            Current = current,
            Points = [.. subgroups.Select(s =>
            {
                var point = points.GetValueOrDefault(s.SampleId);
                var (ucl, lcl) = limits is null || point is null ? (double.NaN, double.NaN) : ControlLimits.At(characteristic.ChartType, limits, point.Size);
                return new BaselinePointDto
                {
                    SampleId = s.SampleId,
                    At = s.At,
                    Value = point?.Value,
                    Dispersion = point?.Dispersion,
                    Size = s.Size,
                    IsLeftOut = leftOut.Contains(s.SampleId),
                    Unusable = unusable.GetValueOrDefault(s.SampleId),
                    IsBeyondLimits = point is not null && limits is not null && (point.Value > ucl || point.Value < lcl
                        || (point.Dispersion is { } d && limits.DispersionUcl is { } du && (d > du || d < (limits.DispersionLcl ?? 0)))),
                };
            })],
        };
    }
}

internal sealed class ApproveControlLimitsHandler(
    IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, IIdGenerator ids, IRequestContext request, TimeProvider timeProvider)
    : ICommandHandler<ApproveControlLimitsCommand, ControlLimitChangeResponse>
{
    public async Task<ControlLimitChangeResponse> HandleAsync(ApproveControlLimitsCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        ControlLimitRules.ValidateWindow(command.From, command.To, now);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var characteristic = await ControlLimitRules.LockCharacteristicAsync(quality, command.CharacteristicId, cancellationToken);
        await ControlLimitRules.ScopeAsync(quality, command.MaterialId, command.EquipmentId, cancellationToken);
        var subgroups = await quality.ListBaselineAsync(command.CharacteristicId, command.MaterialId, command.EquipmentId, command.From, command.To, cancellationToken);
        var leftOut = command.LeftOutSampleIds.Distinct().ToList();
        var result = ControlLimitRules.Work(characteristic, subgroups, leftOut);
        if (result.Limits is not { } limits)
        {
            throw new DomainRuleException(result.NotEnough ?? "The baseline gives no limits.");
        }

        if (!ControlLimitPeriods.Same(limits, new LimitSet(command.CenterLine, command.Ucl, command.Lcl)))
        {
            throw new ConflictException("The baseline's results have changed since you reviewed them, so its limits are different now. Review it again.");
        }

        var limit = new ControlLimit(
            ids.NewId(), characteristic.Id, command.MaterialId, command.EquipmentId, characteristic.ChartType, characteristic.SubgroupSize, limits,
            LimitMethods.Computed, new LimitBaseline(command.From, command.To, result.Subgroups, [.. leftOut.Where(id => subgroups.Any(s => s.SampleId == id))]),
            SpecRules.Text(command.Note), ControlLimitRules.Start(command.EffectiveFrom, now), null, request.ActorId, now);
        var response = await ControlLimitRules.SaveAsync(quality, eventLog, events, limit, "approved", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }
}

internal sealed class SetManualControlLimitsHandler(
    IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, IIdGenerator ids, IRequestContext request, TimeProvider timeProvider)
    : ICommandHandler<SetManualControlLimitsCommand, ControlLimitChangeResponse>
{
    public async Task<ControlLimitChangeResponse> HandleAsync(SetManualControlLimitsCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var characteristic = await ControlLimitRules.LockCharacteristicAsync(quality, command.CharacteristicId, cancellationToken);
        await ControlLimitRules.ScopeAsync(quality, command.MaterialId, command.EquipmentId, cancellationToken);
        var hasDispersion = ChartTypes.HasDispersion(characteristic.ChartType);
        var limit = new ControlLimit(
            ids.NewId(), characteristic.Id, command.MaterialId, command.EquipmentId, characteristic.ChartType, characteristic.SubgroupSize,
            new LimitSet(command.CenterLine, command.Ucl, command.Lcl,
                hasDispersion ? command.DispersionCenter : null, hasDispersion ? command.DispersionUcl : null, hasDispersion ? command.DispersionLcl ?? 0 : null),
            LimitMethods.Manual, null, SpecRules.Text(command.Note), ControlLimitRules.Start(command.EffectiveFrom, now), null, request.ActorId, now);
        var response = await ControlLimitRules.SaveAsync(quality, eventLog, events, limit, "entered", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }
}

internal sealed class EndControlLimitsHandler(IUnitOfWork unitOfWork, IQualityRepository quality, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<EndControlLimitsCommand, ControlLimitChangeResponse>
{
    public async Task<ControlLimitChangeResponse> HandleAsync(EndControlLimitsCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var found = await quality.GetLimitAsync(command.LimitId, cancellationToken) ?? throw new NotFoundException($"Control limits {command.LimitId} not found.");
        await ControlLimitRules.LockCharacteristicAsync(quality, found.CharacteristicId, cancellationToken);
        var limit = await quality.GetLimitAsync(command.LimitId, cancellationToken) ?? found;
        var now = timeProvider.GetUtcNow();
        var ended = limit.End(now);
        var envelope = events.Create(EventTypes.ControlLimitsEnded, "control_limit", limit.Id, new { limit.Id, EndsAt = now, Note = SpecRules.Text(command.Note) });
        await quality.EndLimitAsync(limit.Id, ended.EffectiveTo!.Value, envelope.ActorId, now, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ControlLimitChangeResponse(limit.Id, [limit.Id]);
    }
}
