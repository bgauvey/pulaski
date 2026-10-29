using Pulaski.Application.Abstractions;
using Pulaski.Application.Units;

namespace Pulaski.Application.MachineData;

/// <summary>
/// Rolls up OEE by day (#66): works out each site's production days with the same figuring as the report and keeps every piece of equipment's
/// windows, so a long report adds them up instead of reading the whole history. Days are rolled newest first once their shifts and output
/// are all in; a late change to a day's history marks it, and a change to the site's settings marks all its days, to roll again.
/// </summary>
public sealed class OeeRollups(IOeeReadStore store, IOeeRollupStore rollups, IOeeRollupRepository repository, IUnitReadStore units, IUnitOfWork unitOfWork, TimeProvider time)
{
    /// <summary>How far back days are kept rolled: as far back as a report goes.</summary>
    public const int KeepDays = GetOeeReportHandler.MaxRollupDays;

    /// <summary>Rolls up to <paramref name="limit"/> days that need it, of every site or one; returns how many it rolled.</summary>
    public async Task<int> RollAsync(int limit, CancellationToken cancellationToken, Guid? siteId = null)
    {
        var days = await rollups.ListDaysToRollAsync(time.GetUtcNow(), KeepDays, limit, siteId, cancellationToken);
        foreach (var day in days)
        {
            await RollDayAsync(day, cancellationToken);
        }

        return days.Count;
    }

    public async Task RollDayAsync(OeeDayToRoll day, CancellationToken cancellationToken)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(await store.GetSiteTimeZoneAsync(day.SiteId, cancellationToken) ?? "UTC");
        var equipment = await store.ListEquipmentAsync(day.SiteId, cancellationToken);
        var figures = await OeeFigurer.FigureAsync(
            store, units, day.SiteId, zone, equipment, equipment, day.ProductionDate, day.ProductionDate, time.GetUtcNow(), cancellationToken);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await repository.SaveDayAsync(day, [.. equipment.Select(e => e.Id)], figures, time.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
