using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>A site's label settings as stored (#75, D-080).</summary>
public sealed record LabelSettings(Guid SiteId, string LabelFormat, string? Gs1CompanyPrefix, int SsccExtension, long SsccNextSerial, Guid? DefaultPrinterId);

/// <summary>A label printer as stored.</summary>
public sealed record LabelPrinter(
    Guid Id, Guid SiteId, string Code, string Name, string Method, string? Host, int? Port, string? DropFolder, string TemplateSet, bool IsActive);

/// <summary>A supplier barcode format as stored.</summary>
public sealed record SupplierFormat(Guid Id, Guid SiteId, string Code, string Name, string Pattern, string? ExpiryFormat, int SortOrder, bool IsActive);

/// <summary>Label setup writes (#75), inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ILabelRepository
{
    /// <summary>The site's settings, locked; made with the defaults first when it has none.</summary>
    Task<Versioned<LabelSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken);

    Task<long> UpdateSettingsAsync(LabelSettings settings, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<LabelPrinter>?> GetPrinterForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertPrinterAsync(LabelPrinter printer, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdatePrinterAsync(LabelPrinter printer, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<SupplierFormat>?> GetFormatForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertFormatAsync(SupplierFormat format, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateFormatAsync(SupplierFormat format, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Label setup reads (#75).</summary>
public interface ILabelReadStore
{
    /// <summary>The site's setup, with the defaults when it has saved none.</summary>
    Task<LabelSetupDto> GetSetupAsync(Guid siteId, CancellationToken cancellationToken);

    Task<LabelPrinterDto?> GetPrinterAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The site's active supplier formats, in the order they are tried.</summary>
    Task<IReadOnlyList<SupplierFormat>> ListActiveFormatsAsync(Guid siteId, CancellationToken cancellationToken);
}

public sealed record GetLabelSetupQuery(Guid SiteId) : IQuery<LabelSetupDto>;

public sealed record SetLabelSettingsCommand(Guid SiteId, long ExpectedVersion, string LabelFormat, string? Gs1CompanyPrefix, int SsccExtension, Guid? DefaultPrinterId)
    : ICommand<VersionResponse>;

/// <summary>Adds (no id) or changes a label printer.</summary>
public sealed record SaveLabelPrinterCommand(
    Guid SiteId, Guid? Id, long ExpectedVersion, string Code, string Name, string Method, string? Host, int? Port, string? DropFolder, string TemplateSet, bool IsActive)
    : ICommand<CreatedResponse>;

/// <summary>Adds (no id) or changes a supplier barcode format.</summary>
public sealed record SaveSupplierFormatCommand(
    Guid SiteId, Guid? Id, long ExpectedVersion, string Code, string Name, string Pattern, string? ExpiryFormat, int SortOrder, bool IsActive)
    : ICommand<CreatedResponse>;

/// <summary>What a supplier format would make of a sample; changes nothing.</summary>
public sealed record TestSupplierFormatQuery(string Pattern, string? ExpiryFormat, string Sample) : IQuery<SupplierFormatTestDto>;

public static class LabelFormats
{
    public const string Pulaski = "pulaski";
    public const string Gs1128 = "gs1_128";
    public const string Gs1DataMatrix = "gs1_datamatrix";

    public static IReadOnlyList<string> All { get; } = [Pulaski, Gs1128, Gs1DataMatrix];
}

public static class PrintMethods
{
    public const string RawTcp = "raw_tcp";
    public const string DropFolder = "drop_folder";

    public static IReadOnlyList<string> All { get; } = [RawTcp, DropFolder];
}

internal static class LabelRules
{
    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsCode(string code) => code.Length is >= 1 and <= 30 && char.IsAsciiLetterOrDigit(code[0])
        && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    public static bool IsName(string? name) => name is { Length: >= 1 and <= 50 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>
    /// The SSCC for a new container (#75): from the site's company prefix and the next serial, which it takes (the settings row is locked);
    /// null when the site has no prefix or its serials have run out.
    /// </summary>
    public static async Task<string?> NextSsccAsync(ILabelRepository labels, Guid siteId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var current = await labels.GetSettingsForUpdateAsync(siteId, cancellationToken);
        var settings = current.Value;
        if (settings.Gs1CompanyPrefix is not { } prefix || settings.SsccNextSerial > Gs1.MaxSerial(prefix))
        {
            return null;
        }

        await labels.UpdateSettingsAsync(settings with { SsccNextSerial = settings.SsccNextSerial + 1 }, at, cancellationToken);
        return Gs1.Sscc(prefix, settings.SsccExtension, settings.SsccNextSerial);
    }

    public static ScannedLotDto ToDto(ScannedLot read) => new()
    {
        Source = read.Source,
        MaterialCode = read.MaterialCode,
        Gtin = read.Gtin,
        LotNumber = read.LotNumber,
        SupplierLot = read.SupplierLot,
        Quantity = read.Quantity,
        QuantityUnit = read.QuantityUnit,
        Expiry = read.Expiry,
        Sscc = read.Sscc,
    };
}

internal sealed class GetLabelSetupHandler(ILabelReadStore store) : IQueryHandler<GetLabelSetupQuery, LabelSetupDto>
{
    public Task<LabelSetupDto> HandleAsync(GetLabelSetupQuery query, CancellationToken cancellationToken) => store.GetSetupAsync(query.SiteId, cancellationToken);
}

internal sealed class SetLabelSettingsHandler(IUnitOfWork unitOfWork, ILabelRepository labels, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetLabelSettingsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetLabelSettingsCommand command, CancellationToken cancellationToken)
    {
        var prefix = LabelRules.Text(command.Gs1CompanyPrefix);
        new Validator()
            .Require(LabelFormats.All.Contains(command.LabelFormat), nameof(command.LabelFormat), "pulaski, gs1_128 or gs1_datamatrix.")
            .Require(prefix is null || Gs1.IsCompanyPrefix(prefix), nameof(command.Gs1CompanyPrefix), "6 to 12 digits, as GS1 issued it.")
            .Require(command.SsccExtension is >= 0 and <= 9, nameof(command.SsccExtension), "One digit, 0 to 9.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await labels.GetSettingsForUpdateAsync(command.SiteId, cancellationToken);
        if (command.ExpectedVersion != 0 && current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("The site's label settings were changed by someone else. Reload and try again.");
        }

        if (command.DefaultPrinterId is { } printerId)
        {
            var printer = (await labels.GetPrinterForUpdateAsync(printerId, cancellationToken))?.Value;
            if (printer is not { IsActive: true } || printer.SiteId != command.SiteId)
            {
                throw new DomainRuleException("Choose an active printer of this site as the default.");
            }
        }

        var updated = current.Value with
        {
            LabelFormat = command.LabelFormat,
            Gs1CompanyPrefix = prefix,
            SsccExtension = command.SsccExtension,
            DefaultPrinterId = command.DefaultPrinterId,
        };
        var envelope = events.Create(EventTypes.LabelSettingsSet, "site", command.SiteId, new { Before = current.Value, After = updated });
        var version = await labels.UpdateSettingsAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SaveLabelPrinterHandler(IUnitOfWork unitOfWork, ILabelRepository labels, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<SaveLabelPrinterCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(SaveLabelPrinterCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        var host = LabelRules.Text(command.Host);
        var folder = LabelRules.Text(command.DropFolder);
        var tcp = command.Method == PrintMethods.RawTcp;
        new Validator()
            .Require(command.Id is not null || LabelRules.IsCode(code), "Code", "1 to 30 letters, digits, - or _.")
            .RequireText(name, "Name", 100)
            .Require(PrintMethods.All.Contains(command.Method), "Method", "raw_tcp or drop_folder.")
            .Require(!tcp || host is { Length: <= 255 }, "Host", "The printer's host name or address.")
            .Require(!tcp || command.Port is >= 1 and <= 65535, "Port", "1 to 65535; label printers usually listen on 9100.")
            .Require(tcp || LabelRules.IsName(folder), "DropFolder", "The name of a drop folder in the API's configuration.")
            .Require(LabelRules.IsName(command.TemplateSet), "TemplateSet", "The name of a template set: letters, digits, - or _.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var printer = new LabelPrinter(
            command.Id ?? ids.NewId(), command.SiteId, code, name, command.Method, tcp ? host : null, tcp ? command.Port : null, tcp ? null : folder,
            command.TemplateSet, command.IsActive);
        long version;
        if (command.Id is { } id)
        {
            var current = await labels.GetPrinterForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Printer {id} not found.");
            if (current.Version != command.ExpectedVersion)
            {
                throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
            }

            printer = printer with { SiteId = current.Value.SiteId, Code = current.Value.Code };
            var changed = events.Create(EventTypes.LabelPrinterSaved, "label_printer", printer.Id, printer);
            version = await labels.UpdatePrinterAsync(printer, changed.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(changed, cancellationToken);
        }
        else
        {
            var added = events.Create(EventTypes.LabelPrinterSaved, "label_printer", printer.Id, printer);
            version = await labels.InsertPrinterAsync(printer, added.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(added, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(printer.Id, version);
    }
}

internal sealed class SaveSupplierFormatHandler(IUnitOfWork unitOfWork, ILabelRepository labels, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<SaveSupplierFormatCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(SaveSupplierFormatCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        var expiry = LabelRules.Text(command.ExpiryFormat);
        var problems = SupplierBarcodes.Problems(command.Pattern, expiry);
        new Validator()
            .Require(command.Id is not null || LabelRules.IsCode(code), "Code", "1 to 30 letters, digits, - or _.")
            .RequireText(name, "Name", 100)
            .Require(problems.Count == 0, "Pattern", string.Join(" ", problems))
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var format = new SupplierFormat(command.Id ?? ids.NewId(), command.SiteId, code, name, command.Pattern, expiry, command.SortOrder, command.IsActive);
        long version;
        if (command.Id is { } id)
        {
            var current = await labels.GetFormatForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Supplier format {id} not found.");
            if (current.Version != command.ExpectedVersion)
            {
                throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
            }

            format = format with { SiteId = current.Value.SiteId, Code = current.Value.Code };
            var changed = events.Create(EventTypes.SupplierFormatSaved, "supplier_barcode_format", format.Id, format);
            version = await labels.UpdateFormatAsync(format, changed.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(changed, cancellationToken);
        }
        else
        {
            var added = events.Create(EventTypes.SupplierFormatSaved, "supplier_barcode_format", format.Id, format);
            version = await labels.InsertFormatAsync(format, added.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(added, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(format.Id, version);
    }
}

internal sealed class TestSupplierFormatHandler : IQueryHandler<TestSupplierFormatQuery, SupplierFormatTestDto>
{
    public Task<SupplierFormatTestDto> HandleAsync(TestSupplierFormatQuery query, CancellationToken cancellationToken)
    {
        var expiry = LabelRules.Text(query.ExpiryFormat);
        var problems = SupplierBarcodes.Problems(query.Pattern, expiry);
        return Task.FromResult(problems.Count > 0
            ? new SupplierFormatTestDto { Problems = problems }
            : new SupplierFormatTestDto { Read = SupplierBarcodes.TryRead(query.Sample, query.Pattern, expiry, "test") is { } read ? LabelRules.ToDto(read) : null });
    }
}
