using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Write side for containers (D-059); runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IContainerRepository
{
    Task<Versioned<Container>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    /// <summary>The next label number, so a code made from it matches the label.</summary>
    Task<long> NextLabelNoAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a container with its label number and, when its site numbers containers with one, its GS1 SSCC (#75).</summary>
    Task<long> InsertAsync(Container container, long labelNo, string? sscc, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(Container container, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The ids of the lots with stock in it, in id order, for locking them in that order (D-017).</summary>
    Task<IReadOnlyList<Guid>> ListLotIdsAsync(Guid containerId, CancellationToken cancellationToken);

    /// <summary>The ids of the lots with stock in any of the containers, in id order (#76).</summary>
    Task<IReadOnlyList<Guid>> ListLotIdsInAsync(IReadOnlyCollection<Guid> containerIds, CancellationToken cancellationToken);

    /// <summary>The containers, locked in id order in SQL (#76), so two that lock the same pair can't deadlock.</summary>
    Task<IReadOnlyList<Versioned<Container>>> LockContainersAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Every active container inside it, at any depth (1 = directly inside), locked in id order (#76).</summary>
    Task<IReadOnlyList<(Versioned<Container> Container, int Depth)>> LockInsideAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The containers it is inside, nearest first (#76).</summary>
    Task<IReadOnlyList<Guid>> ListAncestorIdsAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Read side for containers and scans (D-059).</summary>
public interface IContainerReadStore
{
    Task<IReadOnlyList<ContainerDto>> ListAsync(Guid siteId, string? search, bool includeRetired, CancellationToken cancellationToken);

    /// <summary>The container with its lots; null when it doesn't exist.</summary>
    Task<ContainerDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Everything at the site the scan could name: by label number, by location code, or by lot, supplier lot or container code.</summary>
    Task<IReadOnlyList<ScanMatchDto>> FindAsync(Guid siteId, ScanCode code, CancellationToken cancellationToken);

    /// <summary>
    /// What a GS1 or supplier code names at the site (#75): the container with its SSCC, or the lots on hand with its lot (as lot or supplier
    /// lot number), of its material when the code says which; and that material, found by GTIN or code.
    /// </summary>
    Task<(IReadOnlyList<ScanMatchDto> Matches, Guid? MaterialId, string? MaterialCode)> FindScannedAsync(Guid siteId, ScannedLot read, CancellationToken cancellationToken);
}

public sealed record ListContainersQuery(Guid SiteId, string? Search = null, bool IncludeRetired = false) : IQuery<IReadOnlyList<ContainerDto>>;

public sealed record GetContainerQuery(Guid Id) : IQuery<ContainerDto>;

/// <summary>What a scanned code names at a site (D-059): one thing, a choice, or nothing.</summary>
public sealed record ResolveScanQuery(Guid SiteId, string Code) : IQuery<ScanResultDto>;

/// <summary>A new container at a location (D-059). A blank code is made from the kind and its label number.</summary>
public sealed record CreateContainerCommand(Guid SiteId, string? Code, string Kind, Guid LocationId, bool MixesLots = false) : ICommand<CreatedResponse>;

/// <summary>Whether lots mix in a container and how they flow for genealogy (#93); mixing changes only while it is empty.</summary>
public sealed record SetContainerMixingCommand(Guid Id, long ExpectedVersion, bool MixesLots, string FlowModel, int? MinResidenceMinutes) : ICommand<VersionResponse>;

/// <summary>Moves a container and every lot in it (D-059); each lot records its own movement, linked to the container.</summary>
public sealed record MoveContainerCommand(Guid Id, long ExpectedVersion, Guid ToLocationId, string? Comment = null) : ICommand<VersionResponse>;

public sealed record RetireContainerCommand(Guid Id, long ExpectedVersion) : ICommand<VersionResponse>;

/// <summary>Puts a container inside another (#76), moving it and everything in it to that one's location; with no container, takes it out.</summary>
public sealed record PackContainerCommand(Guid Id, long ExpectedVersion, Guid? IntoContainerId, string? Comment = null) : ICommand<VersionResponse>;

/// <summary>Puts a whole lot into a container, moving it there if needed (D-059); to pack part of a lot, split it first.</summary>
public sealed record PackLotCommand(Guid LotId, long ExpectedVersion, Guid ContainerId, string? Comment = null) : ICommand<LotTransactionResponse>;

/// <summary>Takes a lot out of its container; it stays where it is (D-059).</summary>
public sealed record UnpackLotCommand(Guid LotId, long ExpectedVersion, string? Comment = null) : ICommand<LotTransactionResponse>;

internal static class ContainerRules
{
    public static readonly IReadOnlyDictionary<string, string> CodePrefix = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ContainerKinds.Pallet] = "PAL",
        [ContainerKinds.Tote] = "TOTE",
        [ContainerKinds.Box] = "BOX",
        [ContainerKinds.Bin] = "BIN",
        [ContainerKinds.Other] = "CNT",
    };

    /// <summary>
    /// Where a container can be: a location that holds inventory at its site, not one where lots mix into balances (D-031), since
    /// a container keeps its lots apart.
    /// </summary>
    public static async Task<InventoryLocation> LocationAsync(IInventoryRepository inventory, Guid locationId, Guid siteId, CancellationToken cancellationToken)
    {
        var location = await InventoryRules.LocationAsync(inventory, locationId, siteId, cancellationToken);
        return location.ComminglesLots
            ? throw new DomainRuleException($"Lots mix into balances at {location.Code}, so a container can't keep them apart there; choose another location.")
            : location;
    }

    public static async Task<Versioned<Container>> GetAsync(IContainerRepository containers, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var current = await containers.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Container {id} not found.");
        return expectedVersion is { } expected && current.Version != expected
            ? throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.")
            : current;
    }
}

internal sealed class ListContainersHandler(IContainerReadStore store) : IQueryHandler<ListContainersQuery, IReadOnlyList<ContainerDto>>
{
    public Task<IReadOnlyList<ContainerDto>> HandleAsync(ListContainersQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, InventoryRules.Text(query.Search), query.IncludeRetired, cancellationToken);
}

internal sealed class GetContainerHandler(IContainerReadStore store) : IQueryHandler<GetContainerQuery, ContainerDto>
{
    public async Task<ContainerDto> HandleAsync(GetContainerQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Container {query.Id} not found.");
}

internal sealed class ResolveScanHandler(IContainerReadStore store, ILabelReadStore? labels = null, TimeProvider? time = null)
    : IQueryHandler<ResolveScanQuery, ScanResultDto>
{
    public async Task<ScanResultDto> HandleAsync(ResolveScanQuery query, CancellationToken cancellationToken)
    {
        var text = query.Code.Trim();
        new Validator().RequireText(text, "Code", 200).ThrowIfInvalid();
        var code = ScanCode.Parse(text);

        // A GS1 code, or a supplier's in one of the site's formats (#75), says what it is; anything else is matched as a label or text.
        if (code.Kind == ScanCode.FreeText && await ReadAsync(query.SiteId, text, cancellationToken) is { } read)
        {
            var (found, materialId, materialCode) = await store.FindScannedAsync(query.SiteId, read, cancellationToken);
            var parsed = LabelRules.ToDto(read) with { MaterialId = materialId, MaterialCode = materialCode ?? read.MaterialCode };
            var what = read.Source == "gs1" ? "GS1 code" : $"{read.Source} code";
            return found.Count switch
            {
                1 => new ScanResultDto { Code = text, Status = "found", Matches = found, Parsed = parsed },
                > 1 => new ScanResultDto { Code = text, Status = "ambiguous", Matches = found, Parsed = parsed, Message = $"{found.Count} things match this {what}; choose one." },
                _ when materialId is not null => new ScanResultDto
                {
                    Code = text,
                    Status = "receivable",
                    Parsed = parsed,
                    Message = $"A {what} for {materialCode}{LotText(read)}, not at this site yet: receive it?",
                },
                _ => new ScanResultDto
                {
                    Code = text,
                    Status = "not_found",
                    Parsed = parsed,
                    Message = read.Gtin is { } gtin && read.MaterialCode is null
                        ? $"No material has GTIN {gtin}; set it on the material's page."
                        : $"Nothing at this site matches this {what}.",
                },
            };
        }

        var matches = await store.FindAsync(query.SiteId, code, cancellationToken);
        return matches.Count switch
        {
            1 => new ScanResultDto { Code = text, Status = "found", Matches = matches },
            0 => new ScanResultDto
            {
                Code = text,
                Status = "not_found",
                Message = code.Kind == ScanCode.FreeText
                    ? $"Nothing at this site has the number {text}."
                    : $"{text} is a Pulaski label, but nothing at this site has it: it may be from another site, or used up.",
            },
            _ => new ScanResultDto { Code = text, Status = "ambiguous", Matches = matches, Message = $"{matches.Count} things match {text}; choose one." },
        };
    }

    private static string LotText(ScannedLot read) => (read.LotNumber ?? read.SupplierLot) is { } lot ? $", lot {lot}" : "";

    private async Task<ScannedLot?> ReadAsync(Guid siteId, string text, CancellationToken cancellationToken)
    {
        if (Gs1.LooksLikeGs1(text) && Gs1.Parse(text) is { } elements)
        {
            return ScannedLot.FromGs1(Gs1.Read(elements, DateOnly.FromDateTime((time ?? TimeProvider.System).GetUtcNow().UtcDateTime)));
        }

        if (labels is null)
        {
            return null;
        }

        foreach (var format in await labels.ListActiveFormatsAsync(siteId, cancellationToken))
        {
            if (SupplierBarcodes.TryRead(text, format.Pattern, format.ExpiryFormat, format.Code) is { } read)
            {
                return read;
            }
        }

        return null;
    }
}

internal sealed class CreateContainerHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IContainerRepository containers,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    IRequestContext request,
    TimeProvider timeProvider,
    ILabelRepository? labels = null) : ICommandHandler<CreateContainerCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateContainerCommand command, CancellationToken cancellationToken)
    {
        var given = InventoryRules.Text(command.Code)?.ToUpperInvariant();
        new Validator()
            .Require(ContainerKinds.All.Contains(command.Kind), "Kind", $"One of {string.Join(", ", ContainerKinds.All)}.")
            .Require(given is null || given.Length <= Container.MaxCode, "Code", $"At most {Container.MaxCode} characters.")
            .Require(given is null || !given.StartsWith("PUL1", StringComparison.Ordinal), "Code", "Codes starting PUL1 are Pulaski's barcodes.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var location = await ContainerRules.LocationAsync(inventory, command.LocationId, command.SiteId, cancellationToken);
        var labelNo = await containers.NextLabelNoAsync(cancellationToken);
        var code = given ?? $"{ContainerRules.CodePrefix[command.Kind]}-{labelNo}";
        if (await containers.CodeExistsAsync(command.SiteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has a container {code}.");
        }

        var container = Container.Create(ids.NewId(), command.SiteId, code, command.Kind, location.Id, timeProvider.GetUtcNow(), request.ActorId) with
        {
            MixesLots = command.MixesLots,
        };
        // An SSCC from the site's GS1 company prefix, when it has one (#75).
        var sscc = labels is null ? null : await LabelRules.NextSsccAsync(labels, command.SiteId, container.CreatedAt, cancellationToken);
        var envelope = events.Create(EventTypes.ContainerCreated, "container", container.Id, new { After = container, LabelNo = labelNo, Sscc = sscc });
        var version = await containers.InsertAsync(container, labelNo, sscc, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(container.Id, version);
    }
}

/// <summary>
/// Carries a container, every container inside it and all their lots to a location (#76): each container takes the location, and each lot
/// records its own move with the container it is in. The caller has locked the container; this locks those inside, then the lots, in id order.
/// </summary>
internal static class ContainerCarry
{
    public static async Task<(IReadOnlyList<Container> Inside, IReadOnlyList<Guid> LotIds)> CarryAsync(
        IInventoryRepository inventory, IContainerRepository containers, IIdGenerator ids, Container outer, InventoryLocation destination, bool moves,
        string actorId, string? comment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var inside = await containers.LockInsideAsync(outer.Id, cancellationToken);
        var lots = await inventory.LockLotsAsync(await containers.ListLotIdsInAsync([outer.Id, .. inside.Select(c => c.Container.Value.Id)], cancellationToken), cancellationToken);
        if (moves)
        {
            foreach (var materialId in lots.Select(l => l.Lot.MaterialId).Distinct())
            {
                await InventoryRules.EnsureAcceptsAsync(inventory, destination, materialId, cancellationToken);
            }

            foreach (var (container, _) in inside)
            {
                await containers.UpdateAsync(container.Value.CarriedTo(destination.Id), at, cancellationToken);
            }

            foreach (var locked in lots)
            {
                var (after, movement) = locked.Lot.MoveWithContainer(destination.Id);
                await inventory.UpdateLotAsync(after, at, cancellationToken);
                await inventory.InsertEventAsync(
                    new LotEventRow(ids.NewId(), after.Id, movement, after.UomId, actorId, Comment: comment, ContainerId: after.ContainerId), at, cancellationToken);
            }
        }

        return ([.. inside.Select(c => c.Container.Value)], [.. lots.Select(l => l.Lot.Id)]);
    }
}

internal sealed class MoveContainerHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IContainerRepository containers,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<MoveContainerCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(MoveContainerCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator().RequireComment(command.Comment).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The container, then the containers inside it, then all their lots, each in id order: packing locks the same way, so they can't deadlock.
        var current = await ContainerRules.GetAsync(containers, command.Id, command.ExpectedVersion, cancellationToken);
        var destination = await ContainerRules.LocationAsync(inventory, command.ToLocationId, current.Value.SiteId, cancellationToken);
        var moved = current.Value.MoveTo(destination.Id);
        // The event's time and actor stamp every lot's movement; what it carried is known once carried.
        var envelope = events.Create(EventTypes.ContainerMoved, "container", moved.Id, new { });
        var (inside, lotIds) = await ContainerCarry.CarryAsync(
            inventory, containers, ids, moved, destination, moves: true, envelope.ActorId, comment, envelope.OccurredAt, cancellationToken);
        var version = await containers.UpdateAsync(moved, envelope.OccurredAt, cancellationToken);
        var data = new
        {
            moved.Code,
            FromLocationId = current.Value.LocationId,
            ToLocationId = destination.Id,
            FromContainerId = current.Value.ParentId,
            ContainerIds = inside.Select(c => c.Id),
            LotIds = lotIds,
            Comment = comment,
        };
        await eventLog.AppendAsync(envelope with { Data = data }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetContainerMixingHandler(IUnitOfWork unitOfWork, IContainerRepository containers, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetContainerMixingCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetContainerMixingCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.MinResidenceMinutes is null or (>= 0 and <= BalanceGenealogy.MaxResidenceMinutes), "MinResidenceMinutes", "0 to 129600 minutes (90 days), or blank.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ContainerRules.GetAsync(containers, command.Id, command.ExpectedVersion, cancellationToken);
        var inside = (await containers.LockInsideAsync(current.Value.Id, cancellationToken)).Count(c => c.Depth == 1);
        var stock = (await containers.ListLotIdsAsync(current.Value.Id, cancellationToken)).Count;
        var updated = current.Value.SetMixing(
            command.MixesLots, command.FlowModel, command.MinResidenceMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, stock, inside);
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.ContainerMixingSet, "container", updated.Id, new { Before = current.Value, After = updated });
        var version = await containers.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class PackContainerHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IContainerRepository containers,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<PackContainerCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(PackContainerCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator().RequireComment(command.Comment).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (command.IntoContainerId is not { } outerId)
        {
            var current = await ContainerRules.GetAsync(containers, command.Id, command.ExpectedVersion, cancellationToken);
            var outside = current.Value.TakeOut();
            var unpacked = events.Create(EventTypes.ContainerUnpacked, "container", outside.Id, new { outside.Code, FromContainerId = current.Value.ParentId, Comment = comment });
            var unpackedVersion = await containers.UpdateAsync(outside, unpacked.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(unpacked, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new VersionResponse(unpackedVersion);
        }

        // Both containers in id order, then what is inside the one going in, then the lots (#76).
        var locked = await containers.LockContainersAsync([command.Id, outerId], cancellationToken);
        var inner = locked.FirstOrDefault(c => c.Value.Id == command.Id) ?? throw new NotFoundException($"Container {command.Id} not found.");
        var outer = locked.FirstOrDefault(c => c.Value.Id == outerId)?.Value ?? throw new NotFoundException($"Container {outerId} not found.");
        if (inner.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{inner.Value.Code} was changed by someone else. Reload and try again.");
        }

        var height = (await containers.LockInsideAsync(inner.Value.Id, cancellationToken)).Select(c => c.Depth).DefaultIfEmpty(0).Max();
        var packed = inner.Value.PackInto(outer, await containers.ListAncestorIdsAsync(outer.Id, cancellationToken), height);
        var moves = packed.LocationId != inner.Value.LocationId;
        var destination = await ContainerRules.LocationAsync(inventory, packed.LocationId, packed.SiteId, cancellationToken);
        var envelope = events.Create(EventTypes.ContainerPacked, "container", packed.Id, new { packed.Code, IntoContainerId = outer.Id, IntoContainerCode = outer.Code, Comment = comment });
        await ContainerCarry.CarryAsync(inventory, containers, ids, packed, destination, moves, envelope.ActorId, comment, envelope.OccurredAt, cancellationToken);
        var version = await containers.UpdateAsync(packed, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class RetireContainerHandler(
    IUnitOfWork unitOfWork,
    IContainerRepository containers,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<RetireContainerCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RetireContainerCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ContainerRules.GetAsync(containers, command.Id, command.ExpectedVersion, cancellationToken);
        var inside = (await containers.LockInsideAsync(current.Value.Id, cancellationToken)).Count(c => c.Depth == 1);
        var retired = current.Value.Retire((await containers.ListLotIdsAsync(current.Value.Id, cancellationToken)).Count, inside);
        var envelope = events.Create(EventTypes.ContainerRetired, "container", retired.Id, new { Before = current.Value, After = retired });
        var version = await containers.UpdateAsync(retired, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class PackLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IContainerRepository containers,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<PackLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(PackLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator().RequireComment(command.Comment).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var container = (await ContainerRules.GetAsync(containers, command.ContainerId, null, cancellationToken)).Value;
        container.EnsureActive("packed");
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        if (lot.SiteId != container.SiteId)
        {
            throw new DomainRuleException($"{container.Code} is at another site than {lot.Name}.");
        }

        if (lot.LocationId != container.LocationId)
        {
            var location = await ContainerRules.LocationAsync(inventory, container.LocationId, container.SiteId, cancellationToken);
            await InventoryRules.EnsureAcceptsAsync(inventory, location, lot.MaterialId, cancellationToken);
        }

        // Into a container whose lots mix (#93), only available stock goes; it joins the container's balance of the material.
        if (container.MixesLots && lot.Status != LotStatus.Available)
        {
            throw new DomainRuleException($"{lot.Name} is {lot.Status.Replace('_', ' ')}; only available stock goes into {container.Code}, where lots mix.");
        }

        var (after, movement) = lot.PackInto(container.Id, container.LocationId);
        var envelope = events.Create(
            EventTypes.LotPacked, "lot", lot.Id,
            new { lot.LotNumber, ContainerId = container.Id, ContainerCode = container.Code, FromLocationId = lot.LocationId, ToLocationId = container.LocationId, Comment = comment });
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, Comment: comment, ContainerId: container.Id), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        if (container.MixesLots)
        {
            var (balance, balanceVersion, note) = await Commingling.MergeArrivalAsync(
                inventory, eventLog, events, ids, after, container.Code, comment, envelope.OccurredAt, cancellationToken, container.Id);
            await transaction.CommitAsync(cancellationToken);
            return InventoryRules.Response(balance, balanceVersion, [note]);
        }

        await transaction.CommitAsync(cancellationToken);
        return InventoryRules.Response(after, version);
    }
}

internal sealed class UnpackLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<UnpackLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(UnpackLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator().RequireComment(command.Comment).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        var containerId = lot.ContainerId;
        var (after, movement) = lot.Unpack();
        var envelope = events.Create(EventTypes.LotUnpacked, "lot", lot.Id, new { lot.LotNumber, ContainerId = containerId, Comment = comment });
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, Comment: comment, ContainerId: containerId), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return InventoryRules.Response(after, version);
    }
}
