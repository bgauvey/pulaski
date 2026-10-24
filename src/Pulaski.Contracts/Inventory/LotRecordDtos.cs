namespace Pulaski.Contracts.Inventory;

/// <summary>A lot attribute definition (D-056).</summary>
public sealed record LotAttributeDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary><c>text</c>, <c>number</c>, <c>date</c>, <c>boolean</c> or <c>choice</c>; fixed once created.</summary>
    public required string DataType { get; init; }
    public Guid? UomId { get; init; }
    public string? UomCode { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>True: offered for every material; false: only for materials in <see cref="ClassIds"/>.</summary>
    public bool AppliesToAll { get; init; } = true;
    public IReadOnlyList<Guid> ClassIds { get; init; } = [];
    public IReadOnlyList<string> ClassCodes { get; init; } = [];
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }

    /// <summary>How many lots have a value of it.</summary>
    public int LotsWithValue { get; init; }
    public long Version { get; init; }
}

public sealed record CreateLotAttributeRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string DataType { get; init; } = "text";
    public Guid? UomId { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    public bool AppliesToAll { get; init; } = true;
    public IReadOnlyList<Guid> ClassIds { get; init; } = [];
    public int SortOrder { get; init; }
}

public sealed record UpdateLotAttributeRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public Guid? UomId { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    public bool AppliesToAll { get; init; } = true;
    public IReadOnlyList<Guid> ClassIds { get; init; } = [];
    public int SortOrder { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>A lot's attributes and documents (D-056).</summary>
public sealed record LotRecordsDto
{
    public required Guid LotId { get; init; }

    /// <summary>The active attributes its material may have, and any other it has a value of.</summary>
    public IReadOnlyList<LotAttributeValueDto> Attributes { get; init; } = [];

    /// <summary>Its documents, current first; withdrawn ones stay.</summary>
    public IReadOnlyList<LotDocumentDto> Documents { get; init; } = [];
}

public sealed record LotAttributeValueDto
{
    public required Guid AttributeId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public string? UomCode { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The value as text: numbers with a decimal point, dates yyyy-MM-dd, yes/no; null when not recorded.</summary>
    public string? Value { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    /// <summary>False when the attribute is switched off or no longer offered for the material: it can only be cleared.</summary>
    public bool CanSet { get; init; }

    /// <summary>The value's version, 0 before one is recorded; send it back to change it.</summary>
    public long Version { get; init; }
}

/// <summary>Sets or (with a blank value) clears one attribute of a lot.</summary>
public sealed record SetLotAttributeRequest
{
    public long Version { get; init; }
    public string? Value { get; init; }
}

public sealed record LotDocumentDto
{
    public required Guid Id { get; init; }

    /// <summary><c>coa</c>, <c>coc</c>, <c>test_report</c>, <c>sds</c> or <c>other</c>.</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Issuer { get; init; }
    public DateOnly? IssuedOn { get; init; }
    public string? Url { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public int? SizeBytes { get; init; }
    public string? Sha256 { get; init; }

    /// <summary><c>current</c> or <c>withdrawn</c>.</summary>
    public required string Status { get; init; }
    public DateTimeOffset? WithdrawnAt { get; init; }
    public string? WithdrawnBy { get; init; }
    public string? WithdrawReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = "";

    /// <summary>How it came to this lot: <c>attached</c>, or carried from <see cref="FromLotNumber"/> by a <c>split</c> or <c>merge</c>.</summary>
    public string Via { get; init; } = "attached";
    public Guid? FromLotId { get; init; }
    public string? FromLotNumber { get; init; }
    public long Version { get; init; }
}

public sealed record WithdrawLotDocumentRequest
{
    public long Version { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>Lot document kinds and accepted files (D-056).</summary>
public static class LotDocumentFormat
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const string Accept = ".pdf,.png,.jpg,.jpeg,application/pdf,image/png,image/jpeg";
}
