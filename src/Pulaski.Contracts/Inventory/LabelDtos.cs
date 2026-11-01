namespace Pulaski.Contracts.Inventory;

/// <summary>A site's label setup (#75): its format and GS1 numbering, its printers and its supplier barcode formats.</summary>
public sealed record LabelSetupDto
{
    public required Guid SiteId { get; init; }

    /// <summary><c>pulaski</c> (Pulaski's Code 128 only), <c>gs1_128</c> or <c>gs1_datamatrix</c>.</summary>
    public string LabelFormat { get; init; } = "pulaski";

    /// <summary>The site's GS1 company prefix (6 to 12 digits); new containers get SSCCs only once it is set.</summary>
    public string? Gs1CompanyPrefix { get; init; }
    public int SsccExtension { get; init; }

    /// <summary>The serial the next container's SSCC takes.</summary>
    public long SsccNextSerial { get; init; } = 1;
    public Guid? DefaultPrinterId { get; init; }
    public IReadOnlyList<LabelPrinterDto> Printers { get; init; } = [];
    public IReadOnlyList<SupplierBarcodeFormatDto> SupplierFormats { get; init; } = [];

    /// <summary>The settings' version; 0 while the site has none saved.</summary>
    public long Version { get; init; }
}

/// <summary>A label printer: sent the rendered label over raw TCP, or written as a file into a configured drop folder.</summary>
public sealed record LabelPrinterDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary><c>raw_tcp</c> or <c>drop_folder</c>.</summary>
    public required string Method { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }

    /// <summary>The name of a folder in the API's configuration (<c>Labels:DropFolders</c>), never a path.</summary>
    public string? DropFolder { get; init; }

    /// <summary>The folder of label templates under <c>Labels:TemplateDirectory</c>, such as <c>zpl</c>.</summary>
    public required string TemplateSet { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

/// <summary>How a supplier's barcodes read: a pattern with named parts (lot, supplier_lot, material, gtin, qty, expiry).</summary>
public sealed record SupplierBarcodeFormatDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Pattern { get; init; }

    /// <summary>How the expiry part is written, as a .NET date format; null means <c>yyyy-MM-dd</c>.</summary>
    public string? ExpiryFormat { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

/// <summary>The template sets and drop folders the API is configured with, for choosing a printer's.</summary>
public sealed record LabelOptionsDto
{
    public IReadOnlyList<string> TemplateSets { get; init; } = [];
    public IReadOnlyList<string> DropFolders { get; init; } = [];
}

/// <summary>What a scanned GS1 or supplier code said (#75).</summary>
public sealed record ScannedLotDto
{
    /// <summary><c>gs1</c>, or the code of the supplier format that read it.</summary>
    public required string Source { get; init; }
    public Guid? MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    public string? Gtin { get; init; }
    public string? LotNumber { get; init; }
    public string? SupplierLot { get; init; }
    public decimal? Quantity { get; init; }

    /// <summary>The unit the quantity is in when the code said (<c>kg</c>, <c>lb</c>); otherwise the material's base unit is assumed.</summary>
    public string? QuantityUnit { get; init; }
    public DateOnly? Expiry { get; init; }
    public string? Sscc { get; init; }
}

public sealed record SetLabelSettingsRequest
{
    public long Version { get; init; }
    public string LabelFormat { get; init; } = "pulaski";
    public string? Gs1CompanyPrefix { get; init; }
    public int SsccExtension { get; init; }
    public Guid? DefaultPrinterId { get; init; }
}

public sealed record SaveLabelPrinterRequest
{
    public long Version { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Method { get; init; } = "raw_tcp";
    public string? Host { get; init; }
    public int? Port { get; init; } = 9100;
    public string? DropFolder { get; init; }
    public string TemplateSet { get; init; } = "zpl";
    public bool IsActive { get; init; } = true;
}

public sealed record SaveSupplierFormatRequest
{
    public long Version { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Pattern { get; init; } = "";
    public string? ExpiryFormat { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>Tries a supplier format on a sample scan before saving it.</summary>
public sealed record TestSupplierFormatRequest
{
    public string Pattern { get; init; } = "";
    public string? ExpiryFormat { get; init; }
    public string Sample { get; init; } = "";
}

/// <summary>What a supplier format makes of a sample: the problems with it, or what it read (null when the sample doesn't match).</summary>
public sealed record SupplierFormatTestDto
{
    public IReadOnlyList<string> Problems { get; init; } = [];
    public ScannedLotDto? Read { get; init; }
}

/// <summary>Prints labels of one kind (<c>lot</c>, <c>container</c> or <c>location</c>) on a printer; no printer means the site's default.</summary>
public sealed record PrintLabelsRequest
{
    public Guid SiteId { get; init; }
    public Guid? PrinterId { get; init; }
    public string Kind { get; init; } = "lot";
    public IReadOnlyList<Guid> Ids { get; init; } = [];
    public int Copies { get; init; } = 1;
}

/// <summary>What was sent where.</summary>
public sealed record PrintLabelsResponse(int Labels, string PrinterCode, string Method);
