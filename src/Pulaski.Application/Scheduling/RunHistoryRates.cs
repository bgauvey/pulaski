using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Scheduling;

/// <summary>What a planning rate from run history would be (D-045), with its sample, for review; saves nothing.</summary>
public sealed record GetRunHistoryRateQuery(Guid RunStandardId, int Days) : IQuery<RunHistoryRateDto>;

/// <summary>
/// Makes the reviewed history rate the calculated planning rate (D-045). The history is worked out again; if it no longer gives
/// <c>RatePerHour</c> nothing changes (409). A modified rate stays in force.
/// </summary>
public sealed record AdoptHistoryRateCommand(Guid RunStandardId, long ExpectedVersion, int Days, decimal RatePerHour) : ICommand<VersionResponse>;

/// <summary>Takes the calculated planning rate back to the capability's ideal rate, so it follows that rate again (D-034, D-045).</summary>
public sealed record UseIdealRateCommand(Guid RunStandardId, long ExpectedVersion) : ICommand<VersionResponse>;

/// <summary>A run standard with what the history review shows of it.</summary>
public sealed record RunHistoryStandard(
    RunStandard Standard, long Version, string LineCode, string MaterialCode, string UomCode, decimal? IdealRatePerHour, Guid? IdealUomId);

/// <summary>An ended run of the material on the line: its good output in the job's unit.</summary>
public sealed record RunHistoryRow(Guid RunId, string JobNumber, int RunNo, DateTimeOffset StartedAt, DateTimeOffset EndedAt, decimal GoodQty, Guid UomId);

/// <summary>Read side for planning rates from run history (D-045).</summary>
public interface IRunHistoryReadStore
{
    /// <summary>The run standard, with the capability's ideal rate in effect at <paramref name="at"/>; null when it doesn't exist.</summary>
    Task<RunHistoryStandard?> GetStandardAsync(Guid runStandardId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Runs on the line of production jobs of the material that ended in <c>[windowStart, windowEnd)</c>.</summary>
    Task<IReadOnlyList<RunHistoryRow>> ListRunsAsync(Guid lineId, Guid materialId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}

internal static class RunHistory
{
    public static void ValidateDays(int days) =>
        new Validator()
            .Require(days is >= RunHistoryRate.MinDays and <= RunHistoryRate.MaxDays, "Days", $"{RunHistoryRate.MinDays} to {RunHistoryRate.MaxDays} days.")
            .ThrowIfInvalid();

    /// <summary>The runs of the last <paramref name="days"/> worked out into a rate, each run's output in the standard's unit.</summary>
    public static async Task<RunHistoryResult> WorkOutAsync(
        IRunHistoryReadStore store, IUnitReadStore units, RunStandard standard, int days, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var from = now.AddDays(-days);
        var runs = await store.ListRunsAsync(standard.EquipmentId, standard.MaterialId, from, now, cancellationToken);
        var converter = UnitConversions.For(
            standard.MaterialId, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([standard.MaterialId], cancellationToken));
        var samples = runs.Select(r => new RunSample(r.RunId, r.JobNumber, r.RunNo, r.StartedAt, r.EndedAt,
            converter.CanConvert(r.UomId, standard.UomId) ? converter.Convert(r.GoodQty, r.UomId, standard.UomId) : null)).ToList();
        return RunHistoryRate.Calculate(samples, from, now);
    }

    /// <summary>The ideal rate in the standard's unit, when it converts.</summary>
    public static async Task<decimal?> IdealInStandardUnitAsync(IUnitReadStore units, RunHistoryStandard source, CancellationToken cancellationToken)
    {
        if (source.IdealRatePerHour is not { } ideal || source.IdealUomId is not { } uom)
        {
            return null;
        }

        var converter = UnitConversions.For(
            source.Standard.MaterialId, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([source.Standard.MaterialId], cancellationToken));
        return converter.CanConvert(uom, source.Standard.UomId) ? converter.Convert(ideal, uom, source.Standard.UomId) : null;
    }

    public static object Change(RunStandard before, RunStandard after, object? sample = null) => new
    {
        before.EquipmentId,
        before.MaterialId,
        Before = new { before.CalculatedRatePerHour, before.CalculationBasis, before.UomId },
        After = new { after.CalculatedRatePerHour, after.CalculationBasis, after.UomId },
        after.RateSource,
        after.EffectiveRatePerHour,
        Sample = sample,
    };
}

internal sealed class GetRunHistoryRateHandler(IRunHistoryReadStore store, IUnitReadStore units, TimeProvider timeProvider)
    : IQueryHandler<GetRunHistoryRateQuery, RunHistoryRateDto>
{
    public async Task<RunHistoryRateDto> HandleAsync(GetRunHistoryRateQuery query, CancellationToken cancellationToken)
    {
        RunHistory.ValidateDays(query.Days);
        var now = timeProvider.GetUtcNow();
        var source = await store.GetStandardAsync(query.RunStandardId, now, cancellationToken)
            ?? throw new NotFoundException($"Run standard {query.RunStandardId} not found.");
        var result = await RunHistory.WorkOutAsync(store, units, source.Standard, query.Days, now, cancellationToken);
        var standard = source.Standard;

        return new RunHistoryRateDto
        {
            RunStandardId = standard.Id,
            Version = source.Version,
            LineCode = source.LineCode,
            MaterialCode = source.MaterialCode,
            UomCode = source.UomCode,
            Days = query.Days,
            From = result.From,
            To = result.To,
            CalculatedRatePerHour = standard.CalculatedRatePerHour,
            CalculationBasis = standard.CalculationBasis,
            ModifiedRatePerHour = standard.ModifiedRatePerHour,
            EffectiveRatePerHour = standard.EffectiveRatePerHour,
            IdealRatePerHour = await RunHistory.IdealInStandardUnitAsync(units, source, cancellationToken),
            SuggestedRatePerHour = result.RatePerHour,
            NotEnough = result.NotEnough,
            UsedRuns = result.UsedRuns,
            UsedHours = result.UsedHours,
            UsedGoodQty = result.UsedGoodQty,
            SlowFence = result.SlowFence is { } slow ? Math.Round(slow, 2) : null,
            FastFence = result.FastFence is { } fast ? Math.Round(fast, 2) : null,
            MinRuns = RunHistoryRate.MinRuns,
            MinHours = RunHistoryRate.MinHours,
            Runs = [.. result.Runs.Select(v => new RunHistorySampleDto
            {
                RunId = v.Run.RunId,
                JobNumber = v.Run.JobNumber,
                RunNo = v.Run.RunNo,
                StartedAt = v.Run.StartedAt,
                EndedAt = v.Run.EndedAt,
                Hours = Math.Round(v.Run.Hours, 2),
                GoodQty = v.Run.GoodQty,
                RatePerHour = v.RatePerHour is { } r ? Math.Round(r, 2) : null,
                Excluded = v.Excluded,
            })],
        };
    }
}

internal sealed class AdoptHistoryRateHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IRunHistoryReadStore store,
    IUnitReadStore units,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<AdoptHistoryRateCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(AdoptHistoryRateCommand command, CancellationToken cancellationToken)
    {
        RunHistory.ValidateDays(command.Days);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await RunStandards.LockAsync(scheduling, command.RunStandardId, command.ExpectedVersion, cancellationToken);

        // Worked out again under the lock: what is accepted is what was reviewed, or nothing.
        var now = timeProvider.GetUtcNow();
        var result = await RunHistory.WorkOutAsync(store, units, current, command.Days, now, cancellationToken);
        if (result.RatePerHour is not { } rate)
        {
            throw new DomainRuleException(result.NotEnough ?? "The run history gives no rate.");
        }

        if (rate != command.RatePerHour)
        {
            throw new ConflictException($"The run history has changed since you reviewed it: it now gives {rate} an hour. Review it again.");
        }

        var recalculated = current.Recalculate(rate, CalculationBasis.RunHistory, now);
        var envelope = events.Create(EventTypes.RunStandardRecalculated, "run_standard", current.Id, RunHistory.Change(current, recalculated, new
        {
            command.Days,
            result.From,
            result.To,
            result.UsedRuns,
            result.UsedHours,
            result.UsedGoodQty,
            Excluded = result.Runs.Count - result.UsedRuns,
        }));
        var version = await scheduling.UpdateRunStandardCalculationAsync(recalculated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class UseIdealRateHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IRunHistoryReadStore store,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<UseIdealRateCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UseIdealRateCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await RunStandards.LockAsync(scheduling, command.RunStandardId, command.ExpectedVersion, cancellationToken);
        if (current.CalculationBasis == CalculationBasis.IdealRate)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        var now = timeProvider.GetUtcNow();
        var source = await store.GetStandardAsync(current.Id, now, cancellationToken);
        if (source?.IdealRatePerHour is not { } ideal || source.IdealUomId is not { } uom)
        {
            throw new DomainRuleException($"{source?.LineCode ?? "The line"} has no capability in effect for {source?.MaterialCode ?? "the material"}, so there is no ideal rate to go back to.");
        }

        var reset = current.UseIdealRate(ideal, uom, now);
        var envelope = events.Create(EventTypes.RunStandardRecalculated, "run_standard", current.Id, RunHistory.Change(current, reset));
        var version = await scheduling.UpdateRunStandardCalculationAsync(reset, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
