using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Execution;

/// <summary>What lots filled by reports read and change (D-104), inside the report's or the command's transaction.</summary>
public interface ILotFillingStore
{
    /// <summary>The rule of the line's capability for the material in force at <paramref name="at"/>; off when there is none.</summary>
    Task<LotCutRule> GetRuleAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// The lot the job is filling on the line at the location, locked: its latest lot there that is available, loose, not closed, and has had
    /// nothing but the job's production on that line (and reversals of it). Null when that lot can't take more, or there is none.
    /// </summary>
    Task<OpenLot?> GetOpenLotForUpdateAsync(Guid jobId, Guid lineId, Guid locationId, CancellationToken cancellationToken);

    /// <summary>The line's shifts, to tell one shift from the next.</summary>
    Task<EquipmentShifts?> GetShiftsAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>Closes every lot the job made that isn't closed yet (<c>produced_to</c>); returns their numbers, in order.</summary>
    Task<IReadOnlyList<string>> CloseOpenLotsAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>How a report's good output goes into lots on a line that fills them (D-104).</summary>
internal static class LotFilling
{
    /// <summary>
    /// The parts of the good output (in the base unit), each into the open lot or a new one, and a note for each lot closed or started on the
    /// way; null when the line doesn't fill lots for the material, so the report makes one lot as before.
    /// </summary>
    public static async Task<(IReadOnlyList<LotPart> Parts, IReadOnlyList<InventoryWarningDto> Notes)?> PlanAsync(
        ILotFillingStore store, IInventoryRepository inventory, InventoryJob job, InventoryMaterial material, Guid lineId, Guid locationId,
        decimal quantity, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var rule = await store.GetRuleAsync(lineId, material.Id, at, cancellationToken);
        if (!rule.IsOn || !material.IsLotTracked || (await InventoryRules.LocationAsync(inventory, locationId, job.SiteId, cancellationToken)).ComminglesLots)
        {
            return null;
        }

        var open = await store.GetOpenLotForUpdateAsync(job.Id, lineId, locationId, cancellationToken);
        var shifts = rule.ByShift ? await store.GetShiftsAsync(lineId, cancellationToken) : null;
        var lotShift = open is null ? null : ShiftKey(shifts, open.StartedAt);
        var shiftNow = ShiftKey(shifts, at);
        var parts = LotCutting.Parts(rule, open, quantity, at, lotShift, shiftNow);

        var notes = new List<InventoryWarningDto>();
        var started = parts.Count(p => p.LotNumber is null);
        if (open is not null && LotCutting.WhyCut(rule, open, at, lotShift, shiftNow) is { } why)
        {
            notes.Add(new("lot_cut", $"A new lot was started: {why}."));
        }
        else if (open is not null && started > 0)
        {
            notes.Add(new("lot_cut", $"Lot {open.LotNumber} is full ({Qty(rule.Size!.Value)} {material.BaseUomCode}); the rest went into {Lots(started)}."));
        }
        else if (open is null && started > 1)
        {
            notes.Add(new("lot_cut", $"It filled {Lots(started)} of {Qty(rule.Size!.Value)} {material.BaseUomCode}."));
        }

        return (parts, notes);
    }

    /// <summary>One shift's key: its production day and code, else (between shifts, or with no schedule) the site-local day.</summary>
    public static string ShiftKey(EquipmentShifts? shifts, DateTimeOffset at)
    {
        if (shifts is null)
        {
            return DateOnly.FromDateTime(at.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var shift = shifts.Schedule?.ShiftAt(shifts.Zone, at, TimeSpan.Zero);
        return shift is { } s
            ? $"{s.ProductionDate:yyyy-MM-dd}/{s.Code}"
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, shifts.Zone).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Lots(int count) => count == 1 ? "a new lot" : $"{count} new lots";

    private static string Qty(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// Closes the lot a job is filling (D-104), so its next report starts a new one: for a line that fills lots by size, age or shift, when the
/// operator wants a new lot sooner (a new pallet, a quality break).
/// </summary>
public sealed record StartNewLotCommand(Guid JobId, string? Comment = null) : ICommand<StartNewLotResponse>;

public sealed record StartNewLotResponse(Guid JobId, IReadOnlyList<string> ClosedLots);

internal sealed class StartNewLotHandler(
    IUnitOfWork unitOfWork, IInventoryRepository inventory, ILotFillingStore store, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<StartNewLotCommand, StartNewLotResponse>
{
    public async Task<StartNewLotResponse> HandleAsync(StartNewLotCommand command, CancellationToken cancellationToken)
    {
        new Validator().RequireComment(command.Comment).ThrowIfInvalid();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var job = await InventoryRules.JobAsync(inventory, command.JobId, null, cancellationToken);
        var closed = await store.CloseOpenLotsAsync(job.Id, timeProvider.GetUtcNow(), cancellationToken);
        if (closed.Count == 0)
        {
            throw new DomainRuleException($"{job.JobNumber} has no lot being filled; its next report starts a new one anyway.");
        }

        await eventLog.AppendAsync(
            events.Create(EventTypes.JobLotsClosed, "job", job.Id, new { job.JobNumber, LotNumbers = closed, Comment = InventoryRules.Text(command.Comment) }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StartNewLotResponse(job.Id, closed);
    }
}
