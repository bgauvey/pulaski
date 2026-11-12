using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Quality;

/// <summary>The site's SPC alerts: open, and closed in the last 30 days (D-099).</summary>
public sealed record ListSpcAlertsQuery(Guid SiteId) : IQuery<IReadOnlyList<SpcAlertDto>>;

/// <summary>One alert with the results that broke its rules.</summary>
public sealed record GetSpcAlertQuery(Guid SiteId, Guid Id) : IQuery<SpcAlertDto>;

/// <summary>Closes an open alert with its cause (a reason of kind <c>spc_cause</c>) and the action taken. The next break opens a new one.</summary>
public sealed record CloseSpcAlertCommand(Guid SiteId, Guid Id, Guid CauseReasonCodeId, string Action) : ICommand<SpcAlertDto>;

/// <summary>Values match <c>core.spc_alert.status</c>.</summary>
public static class SpcAlertStatus
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>
/// Raises SPC alerts when a result is recorded (D-099): the result's chart (its site, characteristic, material and line) is read back far enough
/// for every rule, and if the result completes a break of the characteristic's run rules against the limits in force for it, the break is
/// kept and added to the chart's open alert, or opens one. Results never hold lots through this; that stays with the specification.
/// </summary>
internal sealed class SpcSignals(IQualityRepository quality, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
{
    /// <returns>The rules broken and the alert, or empty and null.</returns>
    public async Task<(IReadOnlyList<int> Rules, Guid? AlertId, bool IsNew)> CheckAsync(
        Characteristic characteristic, QualityResultRow result, CancellationToken cancellationToken)
    {
        var window = await quality.ListRuleWindowAsync(
            result.SiteId, characteristic.Id, result.MaterialId, result.EquipmentId, result.OccurredAt, result.Id, SpcEvaluation.LookBack, cancellationToken);
        var limits = await quality.GetMaterialLimitsAsync(characteristic.Id, result.MaterialId, cancellationToken);
        var evaluated = SpcEvaluation.Evaluate(characteristic, window, limits);
        if (evaluated.Count == 0 || evaluated[^1].Point.SampleId != result.Id || evaluated[^1] is not { Limit: not null } last)
        {
            return ([], null, false);
        }

        var rules = last.Rules.Union(last.IsDispersionBeyond ? [RunRules.BeyondLimits] : []).Order().ToList();
        if (rules.Count == 0)
        {
            return ([], null, false);
        }

        var signal = new SpcBreak(result.SiteId, characteristic.Id, result.MaterialId, result.EquipmentId, result.Id, result.OccurredAt, rules);
        var now = result.RecordedAt;
        var (alertId, isNew) = await quality.RaiseAlertAsync(signal, ids.NewId(), now, cancellationToken);
        await quality.InsertViolationAsync(signal, ids.NewId(), alertId, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(isNew ? EventTypes.SpcAlertRaised : EventTypes.SpcAlertSignalled, "spc_alert", alertId, new
        {
            AlertId = alertId,
            result.SiteId,
            CharacteristicId = characteristic.Id,
            characteristic.Code,
            result.MaterialId,
            result.EquipmentId,
            SampleId = result.Id,
            SampleAt = result.OccurredAt,
            Rules = rules,
            last.Point.Value,
            Ucl = last.Ucl,
            Lcl = last.Lcl,
            Center = last.Limit.Limits.Center,
        }), cancellationToken);
        return (rules, alertId, isNew);
    }

    /// <summary>What the person recording sees.</summary>
    public static string Note(Characteristic characteristic, IReadOnlyList<int> rules, bool isNew) =>
        $"{characteristic.Code}'s control chart signalled (rule {string.Join(", ", rules)}): "
        + (isNew ? "an SPC alert was raised." : "added to its open SPC alert.");
}

internal sealed class ListSpcAlertsHandler(IQualityReadStore store, TimeProvider timeProvider) : IQueryHandler<ListSpcAlertsQuery, IReadOnlyList<SpcAlertDto>>
{
    public Task<IReadOnlyList<SpcAlertDto>> HandleAsync(ListSpcAlertsQuery query, CancellationToken cancellationToken) =>
        store.ListAlertsAsync(query.SiteId, timeProvider.GetUtcNow().AddDays(-30), cancellationToken);
}

internal sealed class GetSpcAlertHandler(IQualityReadStore store) : IQueryHandler<GetSpcAlertQuery, SpcAlertDto>
{
    public async Task<SpcAlertDto> HandleAsync(GetSpcAlertQuery query, CancellationToken cancellationToken) =>
        await store.GetAlertAsync(query.SiteId, query.Id, cancellationToken) ?? throw new NotFoundException($"SPC alert {query.Id} not found.");
}

internal sealed class CloseSpcAlertHandler(
    IUnitOfWork unitOfWork, IQualityRepository quality, IInventoryRepository inventory, IQualityReadStore store, IEventLog eventLog, IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<CloseSpcAlertCommand, SpcAlertDto>
{
    public const int MaxAction = 1000;

    public async Task<SpcAlertDto> HandleAsync(CloseSpcAlertCommand command, CancellationToken cancellationToken)
    {
        var action = (command.Action ?? "").Trim();
        new Validator()
            .Require(command.CauseReasonCodeId != Guid.Empty, nameof(command.CauseReasonCodeId), "Choose what caused it.")
            .Require(action.Length is > 0 and <= MaxAction, nameof(command.Action), $"Say what was done, at most {MaxAction} characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var alert = await quality.GetAlertForUpdateAsync(command.Id, cancellationToken);
        if (alert is null || alert.SiteId != command.SiteId)
        {
            throw new NotFoundException($"SPC alert {command.Id} not found.");
        }

        if (alert.Status != SpcAlertStatus.Open)
        {
            throw new ConflictException("That alert was closed already.");
        }

        await InventoryRules.ReasonAsync(inventory, command.CauseReasonCodeId, ReasonKinds.SpcCause, alert.SiteId, action, cancellationToken);
        var envelope = events.Create(EventTypes.SpcAlertClosed, "spc_alert", alert.Id, new
        {
            AlertId = alert.Id,
            alert.CharacteristicId,
            alert.Rules,
            alert.Breaks,
            CauseReasonCodeId = command.CauseReasonCodeId,
            Action = action,
        });
        await quality.CloseAlertAsync(alert.Id, command.CauseReasonCodeId, action, envelope.ActorId, timeProvider.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await store.GetAlertAsync(command.SiteId, alert.Id, cancellationToken) ?? throw new NotFoundException($"SPC alert {alert.Id} not found.");
    }
}
