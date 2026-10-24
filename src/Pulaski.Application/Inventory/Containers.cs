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

    Task<long> InsertAsync(Container container, long labelNo, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(Container container, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The ids of the lots with stock in it, in id order, for locking them in that order (D-017).</summary>
    Task<IReadOnlyList<Guid>> ListLotIdsAsync(Guid containerId, CancellationToken cancellationToken);
}

/// <summary>Read side for containers and scans (D-059).</summary>
public interface IContainerReadStore
{
    Task<IReadOnlyList<ContainerDto>> ListAsync(Guid siteId, string? search, bool includeRetired, CancellationToken cancellationToken);

    /// <summary>The container with its lots; null when it doesn't exist.</summary>
    Task<ContainerDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Everything at the site the scan could name: by label number, by location code, or by lot, supplier lot or container code.</summary>
    Task<IReadOnlyList<ScanMatchDto>> FindAsync(Guid siteId, ScanCode code, CancellationToken cancellationToken);
}

public sealed record ListContainersQuery(Guid SiteId, string? Search = null, bool IncludeRetired = false) : IQuery<IReadOnlyList<ContainerDto>>;

public sealed record GetContainerQuery(Guid Id) : IQuery<ContainerDto>;

/// <summary>What a scanned code names at a site (D-059): one thing, a choice, or nothing.</summary>
public sealed record ResolveScanQuery(Guid SiteId, string Code) : IQuery<ScanResultDto>;

/// <summary>A new container at a location (D-059). A blank code is made from the kind and its label number.</summary>
public sealed record CreateContainerCommand(Guid SiteId, string? Code, string Kind, Guid LocationId) : ICommand<CreatedResponse>;

/// <summary>Moves a container and every lot in it (D-059); each lot records its own movement, linked to the container.</summary>
public sealed record MoveContainerCommand(Guid Id, long ExpectedVersion, Guid ToLocationId, string? Comment = null) : ICommand<VersionResponse>;

public sealed record RetireContainerCommand(Guid Id, long ExpectedVersion) : ICommand<VersionResponse>;

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

internal sealed class ResolveScanHandler(IContainerReadStore store) : IQueryHandler<ResolveScanQuery, ScanResultDto>
{
    public async Task<ScanResultDto> HandleAsync(ResolveScanQuery query, CancellationToken cancellationToken)
    {
        var text = query.Code.Trim();
        new Validator().RequireText(text, "Code", 200).ThrowIfInvalid();
        var code = ScanCode.Parse(text);
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
}

internal sealed class CreateContainerHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IContainerRepository containers,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    IRequestContext request,
    TimeProvider timeProvider) : ICommandHandler<CreateContainerCommand, CreatedResponse>
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

        var container = Container.Create(ids.NewId(), command.SiteId, code, command.Kind, location.Id, timeProvider.GetUtcNow(), request.ActorId);
        var envelope = events.Create(EventTypes.ContainerCreated, "container", container.Id, new { After = container, LabelNo = labelNo });
        var version = await containers.InsertAsync(container, labelNo, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(container.Id, version);
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

        // The container, then its lots in id order: packing locks the same way, so the two can't deadlock.
        var current = await ContainerRules.GetAsync(containers, command.Id, command.ExpectedVersion, cancellationToken);
        var destination = await ContainerRules.LocationAsync(inventory, command.ToLocationId, current.Value.SiteId, cancellationToken);
        var moved = current.Value.MoveTo(destination.Id);
        var lots = await inventory.LockLotsAsync(await containers.ListLotIdsAsync(moved.Id, cancellationToken), cancellationToken);
        foreach (var materialId in lots.Select(l => l.Lot.MaterialId).Distinct())
        {
            await InventoryRules.EnsureAcceptsAsync(inventory, destination, materialId, cancellationToken);
        }

        var envelope = events.Create(
            EventTypes.ContainerMoved, "container", moved.Id,
            new { moved.Code, FromLocationId = current.Value.LocationId, ToLocationId = destination.Id, LotIds = lots.Select(l => l.Lot.Id), Comment = comment });
        var version = await containers.UpdateAsync(moved, envelope.OccurredAt, cancellationToken);
        foreach (var locked in lots)
        {
            var (after, movement) = locked.Lot.MoveWithContainer(destination.Id);
            await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(
                new LotEventRow(ids.NewId(), after.Id, movement, after.UomId, envelope.ActorId, Comment: comment, ContainerId: moved.Id),
                envelope.OccurredAt, cancellationToken);
        }

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
        var retired = current.Value.Retire((await containers.ListLotIdsAsync(current.Value.Id, cancellationToken)).Count);
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

        var (after, movement) = lot.PackInto(container.Id, container.LocationId);
        var envelope = events.Create(
            EventTypes.LotPacked, "lot", lot.Id,
            new { lot.LotNumber, ContainerId = container.Id, ContainerCode = container.Code, FromLocationId = lot.LocationId, ToLocationId = container.LocationId, Comment = comment });
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, Comment: comment, ContainerId: container.Id), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
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
