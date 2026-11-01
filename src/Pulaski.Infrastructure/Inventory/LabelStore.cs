using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Label settings, printers and supplier formats inside the unit of work (#75, D-080).</summary>
internal sealed class LabelRepository(DbSession session) : ILabelRepository
{
    private const string EnsureSettings = "INSERT INTO core.label_settings (site_id) VALUES (@SiteId) ON CONFLICT (site_id) DO NOTHING";

    private const string SettingsForUpdate = """
        SELECT site_id, label_format, gs1_company_prefix, sscc_extension, sscc_next_serial, default_printer_id, xmin::text::bigint AS version
        FROM core.label_settings WHERE site_id = @SiteId FOR UPDATE
        """;

    private const string UpdateSettings = """
        UPDATE core.label_settings
        SET label_format = @LabelFormat, gs1_company_prefix = @Gs1CompanyPrefix, sscc_extension = @SsccExtension, sscc_next_serial = @SsccNextSerial,
            default_printer_id = @DefaultPrinterId, updated_at = @At
        WHERE site_id = @SiteId
        RETURNING xmin::text::bigint
        """;

    private const string PrinterForUpdate = """
        SELECT id, site_id, code, name, method, host, port, drop_folder, template_set, is_active, xmin::text::bigint AS version
        FROM core.label_printer WHERE id = @Id FOR UPDATE
        """;

    private const string InsertPrinter = """
        INSERT INTO core.label_printer (id, site_id, code, name, method, host, port, drop_folder, template_set, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @Method, @Host, @Port, @DropFolder, @TemplateSet, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdatePrinter = """
        UPDATE core.label_printer
        SET name = @Name, method = @Method, host = @Host, port = @Port, drop_folder = @DropFolder, template_set = @TemplateSet, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string FormatForUpdate = """
        SELECT id, site_id, code, name, pattern, expiry_format, sort_order, is_active, xmin::text::bigint AS version
        FROM core.supplier_barcode_format WHERE id = @Id FOR UPDATE
        """;

    private const string InsertFormat = """
        INSERT INTO core.supplier_barcode_format (id, site_id, code, name, pattern, expiry_format, sort_order, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @Pattern, @ExpiryFormat, @SortOrder, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateFormat = """
        UPDATE core.supplier_barcode_format
        SET name = @Name, pattern = @Pattern, expiry_format = @ExpiryFormat, sort_order = @SortOrder, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<LabelSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync(EnsureSettings, new { SiteId = siteId }, cancellationToken);
        var r = (await session.QuerySingleOrDefaultAsync<SettingsRow>(SettingsForUpdate, new { SiteId = siteId }, cancellationToken))!;
        return new(new LabelSettings(r.SiteId, r.LabelFormat, r.Gs1CompanyPrefix, r.SsccExtension, r.SsccNextSerial, r.DefaultPrinterId), r.Version);
    }

    public Task<long> UpdateSettingsAsync(LabelSettings settings, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateSettings, new
        {
            settings.SiteId,
            settings.LabelFormat,
            settings.Gs1CompanyPrefix,
            SsccExtension = (short)settings.SsccExtension,
            settings.SsccNextSerial,
            settings.DefaultPrinterId,
            At = at,
        }, cancellationToken);

    public async Task<Versioned<LabelPrinter>?> GetPrinterForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<PrinterRow>(PrinterForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new(new LabelPrinter(r.Id, r.SiteId, r.Code, r.Name, r.Method, r.Host, r.Port, r.DropFolder, r.TemplateSet, r.IsActive), r.Version)
            : null;

    public Task<long> InsertPrinterAsync(LabelPrinter printer, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertPrinter, PrinterArgs(printer, at), cancellationToken);

    public Task<long> UpdatePrinterAsync(LabelPrinter printer, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdatePrinter, PrinterArgs(printer, at), cancellationToken);

    public async Task<Versioned<SupplierFormat>?> GetFormatForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<FormatRow>(FormatForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new(new SupplierFormat(r.Id, r.SiteId, r.Code, r.Name, r.Pattern, r.ExpiryFormat, r.SortOrder, r.IsActive), r.Version)
            : null;

    public Task<long> InsertFormatAsync(SupplierFormat format, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertFormat, FormatArgs(format, at), cancellationToken);

    public Task<long> UpdateFormatAsync(SupplierFormat format, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateFormat, FormatArgs(format, at), cancellationToken);

    private static object PrinterArgs(LabelPrinter p, DateTimeOffset at) =>
        new { p.Id, p.SiteId, p.Code, p.Name, p.Method, p.Host, p.Port, p.DropFolder, p.TemplateSet, p.IsActive, At = at };

    private static object FormatArgs(SupplierFormat f, DateTimeOffset at) =>
        new { f.Id, f.SiteId, f.Code, f.Name, f.Pattern, f.ExpiryFormat, f.SortOrder, f.IsActive, At = at };

    private sealed class SettingsRow
    {
        public Guid SiteId { get; init; }
        public string LabelFormat { get; init; } = LabelFormats.Pulaski;
        public string? Gs1CompanyPrefix { get; init; }
        public short SsccExtension { get; init; }
        public long SsccNextSerial { get; init; }
        public Guid? DefaultPrinterId { get; init; }
        public long Version { get; init; }
    }

    internal sealed class PrinterRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Method { get; init; } = "";
        public string? Host { get; init; }
        public int? Port { get; init; }
        public string? DropFolder { get; init; }
        public string TemplateSet { get; init; } = "";
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    internal sealed class FormatRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Pattern { get; init; } = "";
        public string? ExpiryFormat { get; init; }
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }
}

/// <summary>Label setup reads (#75).</summary>
internal sealed class LabelQueries(NpgsqlDataSource dataSource) : ILabelReadStore
{
    private const string Settings = """
        SELECT label_format, gs1_company_prefix, sscc_extension, sscc_next_serial, default_printer_id, xmin::text::bigint AS version
        FROM core.label_settings WHERE site_id = @SiteId
        """;

    private const string PrinterColumns = "id, site_id, code, name, method, host, port, drop_folder, template_set, is_active, xmin::text::bigint AS version";

    private const string Printers = $"SELECT {PrinterColumns} FROM core.label_printer WHERE site_id = @SiteId ORDER BY is_active DESC, code";

    private const string Printer = $"SELECT {PrinterColumns} FROM core.label_printer WHERE id = @Id";

    private const string Formats = """
        SELECT id, site_id, code, name, pattern, expiry_format, sort_order, is_active, xmin::text::bigint AS version
        FROM core.supplier_barcode_format WHERE site_id = @SiteId
        ORDER BY is_active DESC, sort_order, code
        """;

    public async Task<LabelSetupDto> GetSetupAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { SiteId = siteId };
        var settings = await connection.QuerySingleOrDefaultAsync<SettingsRow>(new CommandDefinition(Settings, args, cancellationToken: cancellationToken));
        var printers = await connection.QueryAsync<LabelPrinterDto>(new CommandDefinition(Printers, args, cancellationToken: cancellationToken));
        var formats = await connection.QueryAsync<SupplierBarcodeFormatDto>(new CommandDefinition(Formats, args, cancellationToken: cancellationToken));
        return new LabelSetupDto
        {
            SiteId = siteId,
            LabelFormat = settings?.LabelFormat ?? LabelFormats.Pulaski,
            Gs1CompanyPrefix = settings?.Gs1CompanyPrefix,
            SsccExtension = settings?.SsccExtension ?? 0,
            SsccNextSerial = settings?.SsccNextSerial ?? 1,
            DefaultPrinterId = settings?.DefaultPrinterId,
            Printers = printers.AsList(),
            SupplierFormats = formats.AsList(),
            Version = settings?.Version ?? 0,
        };
    }

    public async Task<LabelPrinterDto?> GetPrinterAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<LabelPrinterDto>(new CommandDefinition(Printer, new { Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SupplierFormat>> ListActiveFormatsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LabelRepository.FormatRow>(new CommandDefinition(Formats, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return [.. rows.Where(r => r.IsActive).Select(r => new SupplierFormat(r.Id, r.SiteId, r.Code, r.Name, r.Pattern, r.ExpiryFormat, r.SortOrder, r.IsActive))];
    }

    private sealed class SettingsRow
    {
        public string LabelFormat { get; init; } = LabelFormats.Pulaski;
        public string? Gs1CompanyPrefix { get; init; }
        public short SsccExtension { get; init; }
        public long SsccNextSerial { get; init; }
        public Guid? DefaultPrinterId { get; init; }
        public long Version { get; init; }
    }
}
