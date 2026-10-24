using System.Globalization;

namespace Pulaski.Domain.Inventory;

/// <summary>The types a lot attribute can have (D-056).</summary>
public static class LotAttributeTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Boolean = "boolean";
    public const string Choice = "choice";

    public static IReadOnlyList<string> All { get; } = [Text, Number, Date, Boolean, Choice];
}

/// <summary>
/// A property a site records on its lots, such as melt index or moisture (D-056). Offered for every material, or only for materials in
/// <see cref="ClassIds"/>. Its code and type are fixed once created; it is switched off, never deleted.
/// </summary>
public sealed record LotAttributeDefinition(
    Guid Id,
    Guid SiteId,
    string Code,
    string Name,
    string DataType,
    Guid? UomId,
    IReadOnlyList<string> Choices,
    bool AppliesToAll,
    IReadOnlyList<Guid> ClassIds,
    int SortOrder,
    bool IsActive)
{
    public const int MaxCode = 30;
    public const int MaxName = 100;
    public const int MaxChoices = 50;
    public const int MaxText = 500;

    /// <summary>Whether a lot of a material in <paramref name="materialClassId"/> can have it.</summary>
    public bool AppliesTo(Guid? materialClassId) => AppliesToAll || (materialClassId is { } c && ClassIds.Contains(c));

    /// <exception cref="DomainRuleException">The type, unit or choices don't fit together.</exception>
    public static LotAttributeDefinition Create(
        Guid id, Guid siteId, string code, string name, string dataType, Guid? uomId, IReadOnlyList<string> choices, bool appliesToAll,
        IReadOnlyList<Guid> classIds, int sortOrder)
    {
        if (!LotAttributeTypes.All.Contains(dataType))
        {
            throw new DomainRuleException($"'{dataType}' isn't an attribute type; use {string.Join(", ", LotAttributeTypes.All)}.");
        }

        return new LotAttributeDefinition(id, siteId, code, name, dataType, null, [], true, [], 0, IsActive: true)
            .Edit(name, uomId, choices, appliesToAll, classIds, sortOrder, isActive: true);
    }

    /// <summary>New values for everything but the code and type.</summary>
    /// <exception cref="DomainRuleException">A unit on anything but a number, choices on anything but a choice, or classes missing.</exception>
    public LotAttributeDefinition Edit(string name, Guid? uomId, IReadOnlyList<string> choices, bool appliesToAll, IReadOnlyList<Guid> classIds, int sortOrder, bool isActive)
    {
        var cleaned = choices.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (uomId is not null && DataType != LotAttributeTypes.Number)
        {
            throw new DomainRuleException("Only a number attribute has a unit.");
        }

        if ((DataType == LotAttributeTypes.Choice) != (cleaned.Count > 0))
        {
            throw new DomainRuleException(DataType == LotAttributeTypes.Choice ? "A choice attribute needs at least one choice." : "Only a choice attribute has choices.");
        }

        if (cleaned.Count > MaxChoices || cleaned.Any(c => c.Length > MaxName))
        {
            throw new DomainRuleException($"At most {MaxChoices} choices of up to {MaxName} characters.");
        }

        if (!appliesToAll && classIds.Count == 0)
        {
            throw new DomainRuleException("Choose the material classes it is for, or offer it for every material.");
        }

        return this with
        {
            Name = name,
            UomId = uomId,
            Choices = cleaned,
            AppliesToAll = appliesToAll,
            ClassIds = appliesToAll ? [] : [.. classIds.Distinct()],
            SortOrder = sortOrder,
            IsActive = isActive,
        };
    }

    /// <summary>
    /// The value <paramref name="input"/> stands for: numbers with a decimal point, dates as yyyy-MM-dd, yes/no, a choice as listed
    /// (any case). Blank clears the value.
    /// </summary>
    /// <exception cref="DomainRuleException">The input isn't a value of this type.</exception>
    public LotAttributeValue Parse(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            return LotAttributeValue.Empty;
        }

        switch (DataType)
        {
            case LotAttributeTypes.Number:
                return decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                    ? new LotAttributeValue(null, number, null, null)
                    : throw new DomainRuleException($"{Name}: '{text}' isn't a number; use a decimal point and no thousands separator.");
            case LotAttributeTypes.Date:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? new LotAttributeValue(null, null, date, null)
                    : throw new DomainRuleException($"{Name}: '{text}' isn't a date (yyyy-MM-dd).");
            case LotAttributeTypes.Boolean:
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => new LotAttributeValue(null, null, null, true),
                    "false" or "no" or "n" or "0" => new LotAttributeValue(null, null, null, false),
                    _ => throw new DomainRuleException($"{Name}: '{text}' isn't yes or no."),
                };
            case LotAttributeTypes.Choice:
                return Choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase)) is { } choice
                    ? new LotAttributeValue(choice, null, null, null)
                    : throw new DomainRuleException($"{Name}: '{text}' isn't one of {string.Join(", ", Choices)}.");
            default:
                return text.Length <= MaxText
                    ? new LotAttributeValue(text, null, null, null)
                    : throw new DomainRuleException($"{Name}: at most {MaxText} characters.");
        }
    }
}

/// <summary>A lot's value of an attribute, in the field of its type; <see cref="Empty"/> when not recorded or cleared.</summary>
public sealed record LotAttributeValue(string? Text, decimal? Number, DateOnly? Date, bool? Boolean)
{
    public static LotAttributeValue Empty { get; } = new(null, null, null, null);

    public bool IsEmpty => this == Empty;

    /// <summary>The value as an input would give it: invariant numbers, ISO dates, yes/no.</summary>
    public string? Display => Number is { } n ? n.ToString(CultureInfo.InvariantCulture)
        : Date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : Boolean is { } b ? (b ? "yes" : "no")
        : Text;
}

/// <summary>Kinds of lot document (D-056).</summary>
public static class LotDocumentKinds
{
    public const string CertificateOfAnalysis = "coa";
    public const string CertificateOfConformance = "coc";
    public const string TestReport = "test_report";
    public const string SafetyDataSheet = "sds";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [CertificateOfAnalysis, CertificateOfConformance, TestReport, SafetyDataSheet, Other];
}

/// <summary>What a stored lot document may be (D-056): PDF, PNG or JPEG, recognized by its first bytes, never by its name.</summary>
public static class LotDocumentFiles
{
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The content type the bytes are, or null when they are none of the accepted kinds.</summary>
    public static string? Sniff(ReadOnlySpan<byte> content) => content switch
    {
        [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => "application/pdf",                       // %PDF-
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        _ => null,
    };
}

/// <summary>
/// A certificate or other document for one or more lots (D-056): a stored file, a reference to one kept elsewhere, or both. Never
/// deleted; a wrong one is withdrawn with a reason and stays.
/// </summary>
public sealed record LotDocument(
    Guid Id,
    Guid SiteId,
    string Kind,
    string Title,
    string? DocumentNumber,
    string? Issuer,
    DateOnly? IssuedOn,
    string? Url,
    string? FileName,
    string? ContentType,
    int? SizeBytes,
    string? Sha256,
    string Status,
    DateTimeOffset? WithdrawnAt,
    string? WithdrawnBy,
    string? WithdrawReason,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public const string Current = "current";
    public const string Withdrawn = "withdrawn";

    public bool HasFile => FileName is not null;

    /// <exception cref="DomainRuleException">It is already withdrawn.</exception>
    public LotDocument Withdraw(DateTimeOffset at, string by, string reason) => Status == Withdrawn
        ? throw new DomainRuleException($"{Title} is already withdrawn.")
        : this with { Status = Withdrawn, WithdrawnAt = at, WithdrawnBy = by, WithdrawReason = reason };
}
