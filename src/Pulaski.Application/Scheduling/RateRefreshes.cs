using Microsoft.Extensions.Logging;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.MachineData;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Scheduling;

/// <summary>A proposed planning rate (#70, D-098), as <c>core.run_standard_proposal</c> holds it.</summary>
public sealed record RateProposal(
    Guid Id,
    Guid RunStandardId,
    decimal ProposedRatePerHour,
    Guid UomId,
    string CalculationBasis,
    int WindowDays,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    int UsedRuns,
    decimal UsedHours,
    decimal UsedGoodQty,
    decimal StoppedHours,
    decimal CalculatedRatePerHour,
    decimal EffectiveRatePerHour,
    string? ReviewReason,
    DateTimeOffset ProposedAt,
    string Status = ProposalStatus.Pending);

/// <summary>Write side for refreshing planning rates (#70): runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IRateRefreshRepository
{
    /// <summary>The site's settings; the defaults when it has none.</summary>
    Task<RateRefreshSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's settings, locked (a row is made first when it has none, version 0).</summary>
    Task<Versioned<RateRefreshSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken);

    Task<long> UpdateSettingsAsync(RateRefreshSettings settings, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Sites with the scheduled recalculation on, with when it last ran.</summary>
    Task<IReadOnlyList<(RateRefreshSettings Settings, DateTimeOffset? RefreshedAt)>> ListScheduledAsync(CancellationToken cancellationToken);

    Task MarkRefreshedAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The run standards of the site's active lines.</summary>
    Task<IReadOnlyList<Guid>> ListRunStandardsOfSiteAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The run standard's pending proposal, locked; null when it has none.</summary>
    Task<RateProposal?> GetPendingForUpdateAsync(Guid runStandardId, CancellationToken cancellationToken);

    Task InsertProposalAsync(RateProposal proposal, CancellationToken cancellationToken);

    /// <summary>Decides a pending proposal: accepted, auto_accepted, rejected or superseded.</summary>
    Task DecideAsync(Guid proposalId, string status, DateTimeOffset at, string actorId, string? note, CancellationToken cancellationToken);
}

/// <summary>Read side for refreshing planning rates (#70).</summary>
public interface IRateRefreshReadStore
{
    /// <summary>The site's settings; the defaults when it has none.</summary>
    Task<RateRefreshSettingsDto> GetSettingsDtoAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's settings as the rules use them.</summary>
    Task<RateRefreshSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's pending proposals, and those decided since <paramref name="decidedSince"/>, newest first.</summary>
    Task<IReadOnlyList<RateProposalDto>> ListProposalsAsync(Guid siteId, DateTimeOffset decidedSince, CancellationToken cancellationToken);

    /// <summary>The run standard's pending proposal; null when it has none.</summary>
    Task<RateProposalDto?> GetPendingAsync(Guid runStandardId, CancellationToken cancellationToken);
}

public sealed record GetRateRefreshSettingsQuery(Guid SiteId) : IQuery<RateRefreshSettingsDto>;

public sealed record SetRateRefreshSettingsCommand(
    Guid SiteId, long ExpectedVersion, bool TakeOutStops, bool IsScheduled, int EveryDays, int WindowDays, decimal ThresholdPct, bool AutoAccept,
    decimal AutoAcceptLimitPct) : ICommand<VersionResponse>;

/// <summary>The site's proposals waiting for review, and those decided in the last 30 days.</summary>
public sealed record ListRateProposalsQuery(Guid SiteId) : IQuery<IReadOnlyList<RateProposalDto>>;

/// <summary>Accepts a run standard's pending proposal: its history is worked out again, as proposed, under the lock (409 when it changed).</summary>
public sealed record AcceptRateProposalCommand(Guid RunStandardId, long ExpectedVersion, Guid ProposalId) : ICommand<VersionResponse>;

/// <summary>Rejects a run standard's pending proposal; the rate stays as it is.</summary>
public sealed record RejectRateProposalCommand(Guid RunStandardId, Guid ProposalId, string? Note) : ICommand<VersionResponse>;

internal sealed class GetRateRefreshSettingsHandler(IRateRefreshReadStore store) : IQueryHandler<GetRateRefreshSettingsQuery, RateRefreshSettingsDto>
{
    public Task<RateRefreshSettingsDto> HandleAsync(GetRateRefreshSettingsQuery query, CancellationToken cancellationToken) =>
        store.GetSettingsDtoAsync(query.SiteId, cancellationToken);
}

internal sealed class SetRateRefreshSettingsHandler(IUnitOfWork unitOfWork, IRateRefreshRepository refresh, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetRateRefreshSettingsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetRateRefreshSettingsCommand command, CancellationToken cancellationToken)
    {
        var wanted = new RateRefreshSettings(
            command.SiteId, command.TakeOutStops, command.IsScheduled, command.EveryDays, command.WindowDays, command.ThresholdPct, command.AutoAccept,
            command.AutoAcceptLimitPct).Ensure();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await refresh.GetSettingsForUpdateAsync(command.SiteId, cancellationToken);
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("The site's planning rate settings were changed by someone else. Reload and try again.");
        }

        if (wanted == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.RateRefreshSettingsSet, "site", command.SiteId, new { Before = current.Value, After = wanted });
        var version = await refresh.UpdateSettingsAsync(wanted, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class ListRateProposalsHandler(IRateRefreshReadStore store, TimeProvider timeProvider)
    : IQueryHandler<ListRateProposalsQuery, IReadOnlyList<RateProposalDto>>
{
    public Task<IReadOnlyList<RateProposalDto>> HandleAsync(ListRateProposalsQuery query, CancellationToken cancellationToken) =>
        store.ListProposalsAsync(query.SiteId, timeProvider.GetUtcNow().AddDays(-30), cancellationToken);
}

internal sealed class AcceptRateProposalHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IRateRefreshRepository refresh,
    IRunHistoryReadStore store,
    IUnitReadStore units,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider,
    IOeeReadStore? oee = null) : ICommandHandler<AcceptRateProposalCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(AcceptRateProposalCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await RunStandards.LockAsync(scheduling, command.RunStandardId, command.ExpectedVersion, cancellationToken);
        var proposal = await RateProposals.PendingAsync(refresh, command.RunStandardId, command.ProposalId, cancellationToken);

        // Worked out again as it was proposed: the same days and basis must still give the same rate.
        var now = timeProvider.GetUtcNow();
        var source = await store.GetStandardAsync(current.Id, now, cancellationToken) ?? throw new NotFoundException($"Run standard {current.Id} not found.");
        var result = (await RunHistory.WorkOutAsync(
            store, units, oee, current, source.SiteId, source.LineCode, proposal.WindowDays, proposal.CalculationBasis == CalculationBasis.RunHistoryNet, now,
            cancellationToken)).Result;
        if (result.Basis != proposal.CalculationBasis)
        {
            throw new ConflictException($"{source.LineCode}'s stops can no longer be taken out; review the rate again.");
        }

        RunHistory.EnsureSame(result, proposal.ProposedRatePerHour);
        var version = await RunHistory.AdoptAsync(
            scheduling, refresh, eventLog, events, current, result, proposal.WindowDays, now, proposal.Id, ProposalStatus.Accepted, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class RejectRateProposalHandler(
    IUnitOfWork unitOfWork, ISchedulingRepository scheduling, IRateRefreshRepository refresh, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<RejectRateProposalCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RejectRateProposalCommand command, CancellationToken cancellationToken)
    {
        new Validator().RequireComment(command.Note).ThrowIfInvalid();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var standard = await scheduling.GetRunStandardForUpdateAsync(command.RunStandardId, cancellationToken)
            ?? throw new NotFoundException($"Run standard {command.RunStandardId} not found.");
        var proposal = await RateProposals.PendingAsync(refresh, command.RunStandardId, command.ProposalId, cancellationToken);
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        var envelope = events.Create(EventTypes.RunStandardProposalRejected, "run_standard", command.RunStandardId, new
        {
            ProposalId = proposal.Id,
            proposal.ProposedRatePerHour,
            proposal.CalculationBasis,
            Note = note,
        });
        await refresh.DecideAsync(proposal.Id, ProposalStatus.Rejected, timeProvider.GetUtcNow(), envelope.ActorId, note, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(standard.Version);
    }
}

internal static class RateProposals
{
    public static async Task<RateProposal> PendingAsync(IRateRefreshRepository refresh, Guid runStandardId, Guid proposalId, CancellationToken cancellationToken) =>
        await refresh.GetPendingForUpdateAsync(runStandardId, cancellationToken) is { } pending && pending.Id == proposalId
            ? pending
            : throw new ConflictException("That proposal has been decided or replaced since. Reload to see the latest.");
}

/// <summary>
/// The scheduled recalculation (#70, D-098): for each site that switched it on and is due, works out every run standard of its lines over the
/// site's window and basis, and proposes a rate where it moves the calculated rate by more than the site's threshold (a newer proposal
/// replaces the one pending). A site that opted in has small changes to rates already from run history accepted at once, as the system;
/// everything else waits for review. A modified rate stays in force either way. Run by a background service.
/// </summary>
public sealed class RateRefreshRunner(
    IUnitOfWork unitOfWork,
    IRateRefreshRepository refresh,
    ISchedulingRepository scheduling,
    IRunHistoryReadStore store,
    IUnitReadStore units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider time,
    IOeeReadStore? oee = null,
    ILogger<RateRefreshRunner>? logger = null)
{
    /// <returns>The sites recalculated.</returns>
    public async Task<int> RefreshDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(RateRefreshSettings Settings, DateTimeOffset? RefreshedAt)> scheduled;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            scheduled = await refresh.ListScheduledAsync(cancellationToken);
        }

        var due = scheduled.Where(s => RateRefresh.IsDue(s.Settings, s.RefreshedAt, time.GetUtcNow())).Select(s => s.Settings).ToList();
        foreach (var settings in due)
        {
            await RefreshSiteAsync(settings, cancellationToken);
        }

        return due.Count;
    }

    /// <summary>Recalculates one site now, whether due or not.</summary>
    /// <returns>The proposals made.</returns>
    public async Task<int> RefreshSiteAsync(RateRefreshSettings settings, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        IReadOnlyList<Guid> standards;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            standards = await refresh.ListRunStandardsOfSiteAsync(settings.SiteId, cancellationToken);
        }

        var proposed = 0;
        foreach (var id in standards)
        {
            try
            {
                proposed += await ProposeAsync(settings, id, cancellationToken) ? 1 : 0;
            }
            catch (Exception ex) when (ex is DomainRuleException or ConflictException or NotFoundException)
            {
                logger?.LogWarning(ex, "Couldn't recalculate run standard {RunStandardId}.", id);
            }
        }

        await using var marking = await unitOfWork.BeginAsync(cancellationToken);
        await refresh.MarkRefreshedAsync(settings.SiteId, started, cancellationToken);
        await marking.CommitAsync(cancellationToken);
        return proposed;
    }

    private async Task<bool> ProposeAsync(RateRefreshSettings settings, Guid runStandardId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (await store.GetStandardAsync(runStandardId, now, cancellationToken) is not { } source)
        {
            return false;
        }

        var result = (await RunHistory.WorkOutAsync(
            store, units, oee, source.Standard, source.SiteId, source.LineCode, settings.WindowDays, settings.TakeOutStops, now, cancellationToken)).Result;
        if (result.RatePerHour is not { } rate)
        {
            return false;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var locked = await scheduling.GetRunStandardForUpdateAsync(runStandardId, cancellationToken);
        if (locked is null || !RateRefresh.IsWorthProposing(locked.Value, rate, result.Basis, settings.ThresholdPct))
        {
            return false;
        }

        var current = locked.Value;
        var pending = await refresh.GetPendingForUpdateAsync(runStandardId, cancellationToken);
        if (pending is { } same && same.ProposedRatePerHour == rate && same.CalculationBasis == result.Basis)
        {
            return false;
        }

        var why = RateRefresh.WhyReviewed(settings, current, rate);
        var proposal = new RateProposal(
            ids.NewId(), runStandardId, rate, current.UomId, result.Basis, settings.WindowDays, result.From, result.To, result.UsedRuns, result.UsedHours,
            result.UsedGoodQty, result.UsedStoppedHours, current.CalculatedRatePerHour, current.EffectiveRatePerHour, why, now);
        var envelope = events.Create(EventTypes.RunStandardProposed, "run_standard", runStandardId, new
        {
            ProposalId = proposal.Id,
            proposal.ProposedRatePerHour,
            proposal.CalculationBasis,
            proposal.WindowDays,
            proposal.UsedRuns,
            proposal.UsedHours,
            proposal.StoppedHours,
            proposal.CalculatedRatePerHour,
            ReviewReason = why,
            Replaces = pending?.Id,
        });
        if (pending is not null)
        {
            await refresh.DecideAsync(pending.Id, ProposalStatus.Superseded, now, envelope.ActorId, null, cancellationToken);
        }

        await refresh.InsertProposalAsync(proposal, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        if (why is null)
        {
            await RunHistory.AdoptAsync(scheduling, refresh, eventLog, events, current, result, settings.WindowDays, now, proposal.Id, ProposalStatus.AutoAccepted, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
