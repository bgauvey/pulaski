using System.Security.Cryptography;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>A new lot attribute at a site (D-056). Its code and type are fixed once created.</summary>
public sealed record CreateLotAttributeCommand(
    Guid SiteId, string Code, string Name, string DataType, Guid? UomId, IReadOnlyList<string> Choices, bool AppliesToAll,
    IReadOnlyList<Guid> ClassIds, int SortOrder) : ICommand<CreatedResponse>;

public sealed record UpdateLotAttributeCommand(
    Guid Id, long ExpectedVersion, string Name, Guid? UomId, IReadOnlyList<string> Choices, bool AppliesToAll, IReadOnlyList<Guid> ClassIds,
    int SortOrder, bool IsActive) : ICommand<VersionResponse>;

/// <summary>Sets one attribute of a lot, or with a blank value clears it (D-056). The lot itself doesn't change.</summary>
/// <param name="LotId">The lot.</param>
/// <param name="AttributeId">The attribute, of the lot's site.</param>
/// <param name="ExpectedVersion">The value's version as read; 0 when none was recorded.</param>
/// <param name="Value">The value as text (see <see cref="LotAttributeDefinition.Parse"/>); blank to clear it.</param>
public sealed record SetLotAttributeCommand(Guid LotId, Guid AttributeId, long ExpectedVersion, string? Value) : ICommand<VersionResponse>;

/// <summary>Attaches a certificate or other document to a lot (D-056): a file, a reference (number or URL), or both.</summary>
public sealed record AttachLotDocumentCommand(
    Guid LotId, string Kind, string Title, string? DocumentNumber, string? Issuer, DateOnly? IssuedOn, string? Url, string? FileName, byte[]? Content)
    : ICommand<CreatedResponse>;

/// <summary>Withdraws a document that was wrong or is superseded; it stays, with the reason, and its file is kept.</summary>
public sealed record WithdrawLotDocumentCommand(Guid Id, long ExpectedVersion, string Reason) : ICommand<VersionResponse>;

internal static class LotRecordRules
{
    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The classes, when given, are active material classes.</summary>
    public static async Task EnsureClassesAsync(ILotRecordsRepository records, IReadOnlyList<Guid> classIds, CancellationToken cancellationToken)
    {
        var distinct = classIds.Distinct().ToList();
        if (distinct.Count > 0 && await records.CountActiveClassesAsync(distinct, cancellationToken) != distinct.Count)
        {
            throw new DomainRuleException("Choose active material classes.");
        }
    }

    public static async Task EnsureUnitAsync(ILotRecordsRepository records, Guid? uomId, CancellationToken cancellationToken)
    {
        if (uomId is { } id && !await records.IsActiveUnitAsync(id, cancellationToken))
        {
            throw new DomainRuleException("Choose an active unit.");
        }
    }
}

internal sealed class CreateLotAttributeHandler(IUnitOfWork unitOfWork, ILotRecordsRepository records, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateLotAttributeCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateLotAttributeCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        new Validator().RequireText(code, "Code", LotAttributeDefinition.MaxCode).RequireText(name, "Name", LotAttributeDefinition.MaxName).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await records.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("A lot attribute belongs to a site; choose one.");
        }

        if (await records.AttributeCodeExistsAsync(command.SiteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has a lot attribute {code}.");
        }

        await LotRecordRules.EnsureUnitAsync(records, command.UomId, cancellationToken);
        await LotRecordRules.EnsureClassesAsync(records, command.ClassIds, cancellationToken);
        var attribute = LotAttributeDefinition.Create(
            ids.NewId(), command.SiteId, code, name, command.DataType.Trim().ToLowerInvariant(), command.UomId, command.Choices, command.AppliesToAll,
            command.ClassIds, command.SortOrder);
        var envelope = events.Create(EventTypes.LotAttributeCreated, "lot_attribute", attribute.Id, new { After = attribute });
        var version = await records.InsertAttributeAsync(attribute, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(attribute.Id, version);
    }
}

internal sealed class UpdateLotAttributeHandler(IUnitOfWork unitOfWork, ILotRecordsRepository records, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateLotAttributeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateLotAttributeCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        new Validator().RequireText(name, "Name", LotAttributeDefinition.MaxName).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await records.GetAttributeForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Lot attribute {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        if (command.UomId != current.Value.UomId)
        {
            await LotRecordRules.EnsureUnitAsync(records, command.UomId, cancellationToken);
        }

        await LotRecordRules.EnsureClassesAsync(records, [.. command.ClassIds.Except(current.Value.ClassIds)], cancellationToken);
        var updated = current.Value.Edit(name, command.UomId, command.Choices, command.AppliesToAll, command.ClassIds, command.SortOrder, command.IsActive);
        if (Same(updated, current.Value))
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.LotAttributeUpdated, "lot_attribute", updated.Id, new { Before = current.Value, After = updated });
        var version = await records.UpdateAttributeAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }

    // The lists compare by content, not by reference.
    private static bool Same(LotAttributeDefinition a, LotAttributeDefinition b) =>
        a with { Choices = [], ClassIds = [] } == b with { Choices = [], ClassIds = [] }
        && a.Choices.SequenceEqual(b.Choices) && a.ClassIds.Order().SequenceEqual(b.ClassIds.Order());
}

internal sealed class SetLotAttributeHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    ILotRecordsRepository records,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetLotAttributeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetLotAttributeCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The lot first, as every lot change does; its own row isn't changed.
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, null, cancellationToken);
        var context = await records.GetLotContextAsync(lot.Id, cancellationToken) ?? throw new NotFoundException($"Lot {command.LotId} not found.");
        var attribute = (await records.GetAttributeForUpdateAsync(command.AttributeId, cancellationToken))?.Value;
        if (attribute is null || attribute.SiteId != context.SiteId)
        {
            throw new NotFoundException($"Lot attribute {command.AttributeId} not found at {lot.Name}'s site.");
        }

        var current = await records.GetValueForUpdateAsync(lot.Id, attribute.Id, cancellationToken);
        if ((current?.Version ?? 0) != command.ExpectedVersion)
        {
            throw new ConflictException($"{attribute.Name} of {lot.Name} was changed by someone else. Reload and try again.");
        }

        var value = attribute.Parse(command.Value);
        var before = current?.Value ?? LotAttributeValue.Empty;
        if (value == before)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        // Anything recorded can be cleared; a new value needs an attribute that is on and offered for the material.
        if (!value.IsEmpty && (!attribute.IsActive || !attribute.AppliesTo(context.MaterialClassId)))
        {
            throw new DomainRuleException(attribute.IsActive
                ? $"{attribute.Name} isn't recorded for lots of this material's class."
                : $"{attribute.Name} is switched off; it can only be cleared.");
        }

        var envelope = events.Create(
            EventTypes.LotAttributeSet, "lot", lot.Id,
            new { lot.LotNumber, AttributeId = attribute.Id, attribute.Code, Before = before.Display, After = value.Display });
        var version = await records.SaveValueAsync(lot.Id, attribute.Id, value, envelope.ActorId, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class AttachLotDocumentHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    ILotRecordsRepository records,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    IRequestContext request,
    TimeProvider timeProvider) : ICommandHandler<AttachLotDocumentCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(AttachLotDocumentCommand command, CancellationToken cancellationToken)
    {
        var title = LotRecordRules.Text(command.Title);
        var number = LotRecordRules.Text(command.DocumentNumber);
        var issuer = LotRecordRules.Text(command.Issuer);
        var url = LotRecordRules.Text(command.Url);
        var fileName = command.Content is null ? null : Path.GetFileName(LotRecordRules.Text(command.FileName) ?? "document");
        var contentType = command.Content is { } bytes ? LotDocumentFiles.Sniff(bytes) : null;
        new Validator()
            .Require(LotDocumentKinds.All.Contains(command.Kind), "Kind", $"One of {string.Join(", ", LotDocumentKinds.All)}.")
            .RequireText(title, "Title", 200)
            .Require(number is null || number.Length <= 100, "DocumentNumber", "At most 100 characters.")
            .Require(issuer is null || issuer.Length <= 200, "Issuer", "At most 200 characters.")
            .Require(url is null || (url.Length <= 2000 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"), "Url",
                "A full http or https address.")
            .Require(command.Content is null || command.Content.Length is > 0 and <= LotDocumentFiles.MaxBytes, "File",
                $"Up to {LotDocumentFiles.MaxBytes / 1024 / 1024} MB.")
            .Require(command.Content is null || contentType is not null, "File", "A PDF, PNG or JPEG file.")
            .Require(fileName is null || fileName.Length <= 200, "File", "A file name of at most 200 characters.")
            .Require(command.Content is not null || number is not null || url is not null, "File", "Attach a file, or give a document number or address.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, null, cancellationToken);
        var document = new LotDocument(
            ids.NewId(), lot.SiteId, command.Kind, title!, number, issuer, command.IssuedOn, url, fileName, contentType, command.Content?.Length,
            command.Content is { } file ? Convert.ToHexStringLower(SHA256.HashData(file)) : null,
            LotDocument.Current, null, null, null, timeProvider.GetUtcNow(), request.ActorId);
        var envelope = events.Create(EventTypes.LotDocumentAttached, "lot_document", document.Id, new { LotId = lot.Id, lot.LotNumber, After = document });
        var version = await records.InsertDocumentAsync(document, command.Content, lot.Id, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(document.Id, version);
    }
}

internal sealed class WithdrawLotDocumentHandler(
    IUnitOfWork unitOfWork,
    ILotRecordsRepository records,
    IEventLog eventLog,
    IEventFactory events,
    IRequestContext request,
    TimeProvider timeProvider) : ICommandHandler<WithdrawLotDocumentCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(WithdrawLotDocumentCommand command, CancellationToken cancellationToken)
    {
        var reason = LotRecordRules.Text(command.Reason);
        new Validator().RequireText(reason, "Reason", 500).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await records.GetDocumentForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Document {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Title} was changed by someone else. Reload and try again.");
        }

        var withdrawn = current.Value.Withdraw(timeProvider.GetUtcNow(), request.ActorId, reason!);
        var envelope = events.Create(EventTypes.LotDocumentWithdrawn, "lot_document", withdrawn.Id, new { Before = current.Value, After = withdrawn }, reason);
        var version = await records.UpdateDocumentAsync(withdrawn, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
