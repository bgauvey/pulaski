using System.Globalization;
using System.Text.RegularExpressions;

namespace Pulaski.Domain.Inventory;

/// <summary>What a scanned supplier or GS1 code says about a lot (#75); <see cref="Source"/> is <c>gs1</c> or the supplier format's code.</summary>
public sealed record ScannedLot(
    string Source, string? MaterialCode, string? Gtin, string? LotNumber, string? SupplierLot, decimal? Quantity, string? QuantityUnit, DateOnly? Expiry, string? Sscc = null)
{
    public static ScannedLot FromGs1(Gs1Data data) =>
        new("gs1", null, data.Gtin, data.Lot, null, data.Quantity, data.QuantityUnit, data.Expiry, data.Sscc);
}

/// <summary>
/// A site's supplier barcode formats (#75): a regular expression whose named groups say what the parts are: <c>lot</c>, <c>supplier_lot</c>,
/// <c>material</c> (a material code), <c>gtin</c>, <c>qty</c> and <c>expiry</c> (read with the format's date format). The whole scan must
/// match. Patterns run with a time limit, so a bad one can't hang a scan. Pure.
/// </summary>
public static class SupplierBarcodes
{
    public const int MaxPattern = 500;
    public const string DefaultExpiryFormat = "yyyy-MM-dd";
    public static readonly IReadOnlyList<string> Parts = ["lot", "supplier_lot", "material", "gtin", "qty", "expiry"];
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    /// <summary>What is wrong with a format; empty when it can be saved.</summary>
    public static IReadOnlyList<string> Problems(string? pattern, string? expiryFormat)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPattern)
        {
            return [$"A pattern of 1 to {MaxPattern} characters."];
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, Limit);
        }
        catch (ArgumentException ex)
        {
            return [$"The pattern doesn't read as a regular expression: {ex.Message}"];
        }

        var groups = regex.GetGroupNames().Where(g => !int.TryParse(g, out _)).ToList();
        List<string> problems = [];
        if (!groups.Any(g => g is "lot" or "supplier_lot" or "material" or "gtin"))
        {
            problems.Add("Name at least one part that finds a lot or a material: (?<lot>…), (?<supplier_lot>…), (?<material>…) or (?<gtin>…).");
        }

        if (groups.FirstOrDefault(g => !Parts.Contains(g)) is { } unknown)
        {
            problems.Add($"(?<{unknown}>…) isn't a part Pulaski reads; use {string.Join(", ", Parts)}.");
        }

        if (groups.Contains("expiry") && !ValidDateFormat(expiryFormat ?? DefaultExpiryFormat))
        {
            problems.Add("The expiry's date format must be a .NET date format with year, month and day, such as yyMMdd.");
        }

        return problems;
    }

    /// <summary>The lot data in <paramref name="code"/> when the whole scan matches the pattern; null otherwise (or when it took too long).</summary>
    public static ScannedLot? TryRead(string code, string pattern, string? expiryFormat, string source)
    {
        Match match;
        try
        {
            match = Regex.Match(code.Trim(), $"^(?:{pattern})$", RegexOptions.CultureInvariant, Limit);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            return null;
        }

        if (!match.Success)
        {
            return null;
        }

        string? Part(string name) => match.Groups[name] is { Success: true, Value: { Length: > 0 } value } ? value.Trim() : null;
        var quantity = Part("qty") is { } qty && decimal.TryParse(qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) ? q : (decimal?)null;
        var expiry = Part("expiry") is { } text
            && DateOnly.TryParseExact(text, expiryFormat ?? DefaultExpiryFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateOnly?)null;
        return new ScannedLot(source, Part("material"), Gs1.NormalizeGtin(Part("gtin")), Part("lot"), Part("supplier_lot"), quantity, null, expiry);
    }

    private static bool ValidDateFormat(string format) =>
        format.Contains('y', StringComparison.Ordinal) && format.Contains('M', StringComparison.Ordinal) && format.Contains('d', StringComparison.Ordinal)
        && DateOnly.TryParseExact(new DateOnly(2026, 12, 31).ToString(format, CultureInfo.InvariantCulture), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
