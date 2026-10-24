using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Starts a count of a location: a snapshot of its on-hand lots (D-030).</summary>
public sealed record StartStockCountCommand(Guid LocationId, string? Note = null) : ICommand<CreatedResponse>;

/// <summary>Records counted quantities, in each lot's base unit; null clears a line.</summary>
public sealed record RecordCountsCommand(Guid CountId, long ExpectedVersion, IReadOnlyList<CountedLineRequest> Lines) : ICommand<VersionResponse>;

/// <summary>Writes a write-up or write-off for every counted lot that differs, and closes the count.</summary>
public sealed record PostStockCountCommand(Guid CountId, long ExpectedVersion, Guid ReasonCodeId, string? Comment = null) : ICommand<PostStockCountResponse>;

public sealed record CancelStockCountCommand(Guid CountId, long ExpectedVersion) : ICommand<VersionResponse>;

/// <summary>A new reason, enterprise-wide or for one site (D-030); a group holds others and is never chosen (D-062).</summary>
public sealed record CreateReasonCodeCommand(
    Guid? SiteId, string Kind, string Code, string Name, bool RequiresComment, int SortOrder, string? UtilizationClass = null, bool IsGroup = false, Guid? ParentId = null)
    : ICommand<CreatedResponse>;

/// <summary>Files a reason under a group of its kind, or at the top (null) (D-062). History keeps pointing at the same reason.</summary>
public sealed record MoveReasonCodeCommand(Guid Id, long ExpectedVersion, Guid? ParentId) : ICommand<VersionResponse>;

/// <summary>Changes a reason's name, comment flag, order, active flag and utilization class (null keeps it); the code and kind stay.</summary>
public sealed record UpdateReasonCodeCommand(Guid Id, long ExpectedVersion, string Name, bool RequiresComment, int SortOrder, bool IsActive, string? UtilizationClass = null)
    : ICommand<VersionResponse>;

internal sealed class StartStockCountHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<StartStockCountCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(StartStockCountCommand command, CancellationToken cancellationToken)
    {
        var note = InventoryRules.Text(command.Note);
        new Validator()
            .Require(command.LocationId != Guid.Empty, nameof(command.LocationId), "Required.")
            .Require(note is null || note.Length <= InventoryRules.MaxComment, nameof(command.Note), $"At most {InventoryRules.MaxComment} characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var location = await InventoryRules.LocationAsync(inventory, command.LocationId, null, cancellationToken);
        var siteId = location.SiteId!.Value;
        var lots = await inventory.ListOnHandLotsAtAsync(location.Id, cancellationToken);

        var envelope = events.Create(EventTypes.StockCountStarted, "stock_count", ids.NewId(), new { });
        var sequence = await inventory.CountStockCountsOnAsync(siteId, envelope.OccurredAt, cancellationToken) + 1;
        var count = new StockCount(envelope.Subject, siteId, StockCount.NumberFor(envelope.OccurredAt, sequence), location.Id, StockCountStatus.Open, note);
        var lines = lots.Select(l => new StockCountLine(ids.NewId(), count.Id, l.Id, l.Quantity, null, null, null)).ToList();
        envelope = envelope with { Data = new { count.Number, LocationId = location.Id, Lots = lines.Count, Note = note } };

        var version = await inventory.InsertStockCountAsync(count, envelope.ActorId, envelope.OccurredAt, cancellationToken);
        await inventory.InsertStockCountLinesAsync(lines, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(count.Id, version);
    }
}

internal sealed class RecordCountsHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<RecordCountsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RecordCountsCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Lines.Count > 0, nameof(command.Lines), "Nothing to record.")
            .Require(command.Lines.All(l => l.CountedQty is null or >= 0), nameof(command.Lines), "A count can't be negative.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var count = await StockCountLocks.CountAsync(inventory, command.CountId, command.ExpectedVersion, cancellationToken);
        count.EnsureOpen();
        var lines = (await inventory.GetStockCountLinesAsync(count.Id, cancellationToken)).ToDictionary(l => l.Id);

        var envelope = events.Create(EventTypes.StockCountRecorded, "stock_count", count.Id, new { count.Number, Lines = command.Lines });
        foreach (var entry in command.Lines)
        {
            var line = lines.GetValueOrDefault(entry.LineId)
                ?? throw new DomainRuleException($"Line {entry.LineId} isn't in count {count.Number}.");
            await inventory.UpdateStockCountLineAsync(
                entry.CountedQty is { } counted
                    ? line with { CountedQty = Math.Round(counted, 6, MidpointRounding.AwayFromZero), CountedAt = envelope.OccurredAt, CountedBy = envelope.ActorId }
                    : line with { CountedQty = null, CountedAt = null, CountedBy = null },
                cancellationToken);
        }

        var version = await inventory.UpdateStockCountAsync(count, envelope.ActorId, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class PostStockCountHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<PostStockCountCommand, PostStockCountResponse>
{
    public async Task<PostStockCountResponse> HandleAsync(PostStockCountCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.ReasonCodeId != Guid.Empty, nameof(command.ReasonCodeId), "Choose a reason.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var count = await StockCountLocks.CountAsync(inventory, command.CountId, command.ExpectedVersion, cancellationToken);
        count.EnsureOpen();
        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, count.SiteId, comment, cancellationToken);
        var lines = await inventory.GetStockCountLinesAsync(count.Id, cancellationToken);
        var lots = (await inventory.LockLotsAsync([.. lines.Select(l => l.LotId)], cancellationToken))
            .ToDictionary(l => l.Lot.Id, l => new CountedLot(l.Lot, l.UpdatedAt));

        var (adjustments, problems) = StockCountPosting.Plan(lines, lots);
        if (problems.Count > 0)
        {
            throw new DomainRuleException($"Count {count.Number} can't be posted yet. " + string.Join(" ", problems));
        }

        var unitCodes = new Dictionary<Guid, string>();
        foreach (var adjustment in adjustments)
        {
            var lot = adjustment.Lot;
            if (!unitCodes.TryGetValue(lot.MaterialId, out var unitCode))
            {
                unitCodes[lot.MaterialId] = unitCode = (await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken)).BaseUomCode;
            }

            var (after, movement) = adjustment.Variance > 0
                ? lot.Add(adjustment.Variance, LotEventKind.Adjusted)
                : lot.Take(-adjustment.Variance, LotEventKind.Scrapped, unitCode);
            var envelope = events.Create(
                EventTypes.LotCountAdjusted, "lot", lot.Id,
                new { lot.LotNumber, CountId = count.Id, CountNumber = count.Number, Counted = adjustment.Line.CountedQty, adjustment.Variance, QuantityAfter = after.Quantity, ReasonCodeId = reason.Id, Comment = comment },
                reason.Code);
            await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(
                new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reason.Id, Comment: comment, Reference: count.Number),
                envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
        }

        var posted = events.Create(
            EventTypes.StockCountPosted, "stock_count", count.Id,
            new { count.Number, Lines = lines.Count, Adjusted = adjustments.Count, ReasonCodeId = reason.Id, Comment = comment }, reason.Code);
        var version = await inventory.UpdateStockCountAsync(count.Post(), posted.ActorId, posted.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(posted, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PostStockCountResponse(version, adjustments.Count);
    }
}

internal sealed class CancelStockCountHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<CancelStockCountCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(CancelStockCountCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var count = await StockCountLocks.CountAsync(inventory, command.CountId, command.ExpectedVersion, cancellationToken);
        var envelope = events.Create(EventTypes.StockCountCancelled, "stock_count", count.Id, new { count.Number });
        var version = await inventory.UpdateStockCountAsync(count.Cancel(), envelope.ActorId, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class CreateReasonCodeHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateReasonCodeCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateReasonCodeCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        new Validator()
            .Require(ReasonCode.Kinds.Contains(command.Kind), nameof(command.Kind), "Not a reason kind.")
            .Require(ReasonCode.IsValidCode(code), nameof(command.Code), "Letters, digits, '-', '_' or '.', up to 30.")
            .RequireText(name, nameof(command.Name), 100)
            .Require(command.SortOrder is >= 0 and <= 10_000, nameof(command.SortOrder), "Between 0 and 10000.")
            .Require(!ReasonCode.Kinds.Contains(command.Kind) || ReasonCode.IsValidClass(command.Kind, command.UtilizationClass, command.IsGroup), nameof(command.UtilizationClass),
                ReasonCodeRules.ClassMessage(command.Kind, command.IsGroup))
            .Require(!(command.IsGroup && command.RequiresComment), nameof(command.RequiresComment), "A group is never chosen, so it never needs a comment.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (command.SiteId is { } siteId && !await inventory.IsSiteAsync(siteId, cancellationToken))
        {
            throw new DomainRuleException($"Site {siteId} not found.");
        }

        var reason = new ReasonCode(ids.NewId(), command.SiteId, command.Kind, code, name, command.RequiresComment, command.SortOrder, IsActive: true,
            command.UtilizationClass, command.ParentId, command.IsGroup);
        if (command.ParentId is not null)
        {
            var tree = await ReasonCodeRules.TreeAsync(inventory, command.Kind, cancellationToken);
            if (ReasonTree.WhyNotUnder(tree, reason, command.ParentId) is { } why)
            {
                throw new DomainRuleException(why);
            }
        }

        var envelope = events.Create(EventTypes.ReasonCodeCreated, "reason_code", reason.Id, new { After = reason });
        var version = await inventory.InsertReasonCodeAsync(reason, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(reason.Id, version);
    }
}

internal sealed class UpdateReasonCodeHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateReasonCodeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateReasonCodeCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        new Validator()
            .RequireText(name, nameof(command.Name), 100)
            .Require(command.SortOrder is >= 0 and <= 10_000, nameof(command.SortOrder), "Between 0 and 10000.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (current, tree) = await ReasonCodeRules.LockAsync(inventory, command.Id, command.ExpectedVersion, cancellationToken);
        var after = current with
        {
            Name = name,
            RequiresComment = command.RequiresComment,
            SortOrder = command.SortOrder,
            IsActive = command.IsActive,
            UtilizationClass = current.IsGroup ? null : command.UtilizationClass ?? current.UtilizationClass,
        };
        if (!ReasonCode.IsValidClass(after.Kind, after.UtilizationClass, after.IsGroup))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.UtilizationClass)] = [ReasonCodeRules.ClassMessage(after.Kind, after.IsGroup)],
            });
        }

        if (after.IsGroup && after.RequiresComment)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.RequiresComment)] = ["A group is never chosen, so it never needs a comment."],
            });
        }

        if (ReasonTree.WhyNotActive(tree, current, after.IsActive) is { } why)
        {
            throw new DomainRuleException(why);
        }

        var envelope = events.Create(EventTypes.ReasonCodeUpdated, "reason_code", after.Id, new { Before = current, After = after });
        var version = await inventory.UpdateReasonCodeAsync(after, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class MoveReasonCodeHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<MoveReasonCodeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(MoveReasonCodeCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (current, tree) = await ReasonCodeRules.LockAsync(inventory, command.Id, command.ExpectedVersion, cancellationToken);
        if (current.ParentId == command.ParentId)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        if (ReasonTree.WhyNotUnder(tree, current, command.ParentId) is { } why)
        {
            throw new DomainRuleException(why);
        }

        var after = current with { ParentId = command.ParentId };
        var envelope = events.Create(EventTypes.ReasonCodeMoved, "reason_code", after.Id, new { after.Code, Before = current.ParentId, After = after.ParentId });
        var version = await inventory.UpdateReasonCodeAsync(after, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal static class ReasonCodeRules
{
    public static string ClassMessage(string kind, bool isGroup) =>
        kind != ReasonCode.Utilization ? "Only a utilization reason has a class."
        : isGroup ? "A group has no class: the reasons in it each have their own."
        : "Runtime, downtime or planned.";

    public static async Task<IReadOnlyList<ReasonCode>> TreeAsync(IInventoryRepository inventory, string kind, CancellationToken cancellationToken) =>
        [.. (await inventory.LockReasonTreeAsync(kind, cancellationToken)).Select(r => r.Value)];

    /// <summary>Locks the reason's whole kind (D-062), then checks the caller's version of the reason.</summary>
    public static async Task<(ReasonCode Reason, IReadOnlyList<ReasonCode> Tree)> LockAsync(
        IInventoryRepository inventory, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var kind = await inventory.GetReasonKindAsync(id, cancellationToken) ?? throw new NotFoundException($"Reason {id} not found.");
        var locked = await inventory.LockReasonTreeAsync(kind, cancellationToken);
        var current = locked.FirstOrDefault(r => r.Value.Id == id) ?? throw new NotFoundException($"Reason {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"Reason {current.Value.Code} was changed by someone else. Reload and try again.");
        }

        return (current.Value, [.. locked.Select(r => r.Value)]);
    }
}

internal static class StockCountLocks
{
    public static async Task<StockCount> CountAsync(IInventoryRepository inventory, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await inventory.GetStockCountForUpdateAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Count {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"Count {current.Value.Number} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }
}

/// <summary>Sets or clears a material's shelf life in days (D-032); new lots expire that long after they arrive.</summary>
public sealed record SetShelfLifeCommand(Guid MaterialId, long ExpectedVersion, int? ShelfLifeDays) : ICommand<VersionResponse>;

internal sealed class SetShelfLifeHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetShelfLifeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetShelfLifeCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.ShelfLifeDays is null or (> 0 and <= 36_500), nameof(command.ShelfLifeDays), "Between 1 and 36500 days, or blank for none.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await inventory.GetMaterialForUpdateAsync(command.MaterialId, cancellationToken)
            ?? throw new NotFoundException($"Material {command.MaterialId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var envelope = events.Create(
            EventTypes.MaterialShelfLifeSet, "material", current.Value.Id,
            new { current.Value.Code, Before = current.Value.ShelfLifeDays, After = command.ShelfLifeDays });
        var version = await inventory.UpdateShelfLifeAsync(current.Value.Id, command.ShelfLifeDays, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}
