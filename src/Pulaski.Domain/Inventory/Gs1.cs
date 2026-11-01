using System.Globalization;
using System.Text;

namespace Pulaski.Domain.Inventory;

/// <summary>One GS1 element: an application identifier and its value, such as (10) and a lot number.</summary>
public sealed record Gs1Element(string Ai, string Value);

/// <summary>What a GS1 code says about a lot or a logistic unit (#75), from the application identifiers Pulaski reads.</summary>
public sealed record Gs1Data(string? Sscc, string? Gtin, string? Lot, string? Serial, DateOnly? Expiry, decimal? Quantity, string? QuantityUnit);

/// <summary>
/// GS1 identifiers and element strings (#75), from the public GS1 General Specifications: the mod-10 check digit, GTINs, SSCCs, and element
/// strings written as people read them, <c>(01)09501101530003(10)ABC</c>, or as scanners send them, with a symbology identifier (<c>]C1</c>,
/// <c>]d2</c>, <c>]Q3</c>, <c>]e0</c>) and a group separator after each variable-length field. Pure.
/// </summary>
public static class Gs1
{
    /// <summary>The group separator (ASCII 29) that ends a variable-length field in transmitted data.</summary>
    public const char GroupSeparator = '\u001D';

    public const int MaxLot = 20;

    /// <summary>The check digit of <paramref name="digits"/> (all but the last digit of a GTIN or SSCC).</summary>
    public static int CheckDigit(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? digit * 3 : digit;
        }

        return (10 - sum % 10) % 10;
    }

    /// <summary>A GTIN-8, -12, -13 or -14 as 14 digits, when its check digit is right; null otherwise.</summary>
    public static string? NormalizeGtin(string? text)
    {
        var digits = text?.Trim();
        if (digits is null || digits.Length is not (8 or 12 or 13 or 14) || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var gtin = digits.PadLeft(14, '0');
        return CheckDigit(gtin[..13]) == gtin[13] - '0' ? gtin : null;
    }

    /// <summary>A company prefix as GS1 issues them: 6 to 12 digits.</summary>
    public static bool IsCompanyPrefix(string? text) => text is { Length: >= 6 and <= 12 } && text.All(char.IsAsciiDigit);

    /// <summary>The largest serial an SSCC can carry after this company prefix.</summary>
    public static long MaxSerial(string companyPrefix) => (long)Math.Pow(10, 16 - companyPrefix.Length) - 1;

    /// <summary>An SSCC: extension digit, company prefix, serial reference, check digit; 18 digits.</summary>
    public static string Sscc(string companyPrefix, int extension, long serial)
    {
        if (!IsCompanyPrefix(companyPrefix) || extension is < 0 or > 9 || serial < 0 || serial > MaxSerial(companyPrefix))
        {
            throw new DomainRuleException("The SSCC's company prefix, extension digit or serial is out of range.");
        }

        var body = extension.ToString(CultureInfo.InvariantCulture) + companyPrefix + serial.ToString(CultureInfo.InvariantCulture).PadLeft(16 - companyPrefix.Length, '0');
        return body + CheckDigit(body).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a value may go in a GS1 alphanumeric field (the 82 characters of GS1 character set 82).</summary>
    public static bool IsCset82(string value) =>
        value.All(c => c is >= '!' and <= 'z' && c is not ('#' or '$' or '@' or '[' or '\\' or ']' or '^' or '`'));

    /// <summary>The elements as people read them: <c>(01)…(10)…</c>.</summary>
    public static string HumanReadable(IEnumerable<Gs1Element> elements) => string.Concat(elements.Select(e => $"({e.Ai}){e.Value}"));

    /// <summary>
    /// The elements as encoded: fixed-length fields first is the caller's business; each variable-length field but the last is followed by
    /// <paramref name="separator"/> (a group separator, or the escape a printer language uses for FNC1).
    /// </summary>
    public static string Data(IReadOnlyList<Gs1Element> elements, string separator)
    {
        var data = new StringBuilder();
        for (var i = 0; i < elements.Count; i++)
        {
            data.Append(elements[i].Ai).Append(elements[i].Value);
            if (i < elements.Count - 1 && FixedLength(elements[i].Ai) is null)
            {
                data.Append(separator);
            }
        }

        return data.ToString();
    }

    /// <summary>
    /// Whether the scan looks like GS1 rather than text: a GS1 symbology identifier, the bracketed form, a group separator, or digits starting
    /// (00), (01) or (02) that read through to the end.
    /// </summary>
    public static bool LooksLikeGs1(string scanned)
    {
        var text = scanned.Trim();
        return text.StartsWith("]C1", StringComparison.Ordinal) || text.StartsWith("]d2", StringComparison.Ordinal)
            || text.StartsWith("]Q3", StringComparison.Ordinal) || text.StartsWith("]e0", StringComparison.Ordinal)
            || text.StartsWith("(0", StringComparison.Ordinal) || text.StartsWith("(1", StringComparison.Ordinal) || text.Contains(GroupSeparator, StringComparison.Ordinal)
            || (text.Length >= 16 && (text.StartsWith("00", StringComparison.Ordinal) || text.StartsWith("01", StringComparison.Ordinal) || text.StartsWith("02", StringComparison.Ordinal))
                && Parse(text) is not null);
    }

    /// <summary>The elements of a scanned or typed GS1 code; null when it doesn't read as GS1 all the way through.</summary>
    public static IReadOnlyList<Gs1Element>? Parse(string scanned)
    {
        var text = scanned.Trim();
        if (text.Length >= 3 && text[0] == ']')
        {
            text = text[3..];
        }

        return text.StartsWith('(') ? ParseBracketed(text) : ParseRaw(text);
    }

    /// <summary>
    /// What the elements say about a lot or a unit; quantities from (30), (37) or a net weight (310n kg, 320n lb). A two-digit year is read
    /// against <paramref name="today"/>.
    /// </summary>
    public static Gs1Data Read(IEnumerable<Gs1Element> elements, DateOnly today)
    {
        string? sscc = null, gtin = null, lot = null, serial = null, unit = null;
        DateOnly? expiry = null;
        decimal? quantity = null;
        foreach (var e in elements)
        {
            switch (e.Ai)
            {
                case "00":
                    sscc = e.Value;
                    break;
                case "01" or "02":
                    gtin ??= e.Value;
                    break;
                case "10":
                    lot = e.Value;
                    break;
                case "21":
                    serial = e.Value;
                    break;
                case "17":
                    expiry = Date(e.Value, today.Year);
                    break;
                case "30" or "37" when decimal.TryParse(e.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count):
                    quantity ??= count;
                    break;
                case var ai when ai.Length == 4 && (ai.StartsWith("310", StringComparison.Ordinal) || ai.StartsWith("320", StringComparison.Ordinal))
                    && long.TryParse(e.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var raw):
                    quantity = raw / (decimal)Math.Pow(10, ai[3] - '0');
                    unit = ai.StartsWith("310", StringComparison.Ordinal) ? "kg" : "lb";
                    break;
            }
        }

        return new Gs1Data(sscc, gtin, lot, serial, expiry, quantity, unit);
    }

    /// <summary>A net weight as (310n) in kilograms or (320n) in pounds, with as many decimals as it needs (at most 5) in six digits; null when it won't fit.</summary>
    public static Gs1Element? NetWeight(decimal quantity, bool pounds)
    {
        for (var decimals = 0; decimals <= 5; decimals++)
        {
            var scaled = quantity * (decimal)Math.Pow(10, decimals);
            if (scaled == decimal.Truncate(scaled))
            {
                return scaled is > 0 and <= 999_999
                    ? new Gs1Element((pounds ? "320" : "310") + decimals.ToString(CultureInfo.InvariantCulture), ((long)scaled).ToString("D6", CultureInfo.InvariantCulture))
                    : null;
            }
        }

        return null;
    }

    /// <summary>YYMMDD for (17); day 00 means the last day of the month.</summary>
    public static string DateValue(DateOnly date) => date.ToString("yyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// How long an AI's value is when fixed (the predefined lengths of the General Specifications); null when variable. The AI's own
    /// length comes from <see cref="AiLength"/>.
    /// </summary>
    public static int? FixedLength(string ai) => ai[..2] switch
    {
        "00" => 18,
        "01" or "02" or "03" => 14,
        "04" => 16,
        "11" or "12" or "13" or "14" or "15" or "16" or "17" or "18" or "19" => 6,
        "20" => 2,
        "31" or "32" or "33" or "34" or "35" or "36" => 6,
        "41" => 13,
        _ => null,
    };

    /// <summary>How many digits an AI starting with these two has.</summary>
    public static int AiLength(string firstTwo) => firstTwo switch
    {
        "23" or "24" or "25" or "40" or "41" or "42" => 3,
        "31" or "32" or "33" or "34" or "35" or "36" or "39" or "70" or "71" or "72" or "80" or "81" or "82" => 4,
        _ => 2,
    };

    private static int MaxVariable(string ai) => ai switch
    {
        "10" or "21" or "22" => 20,
        "30" or "37" => 8,
        _ => 90,
    };

    private static List<Gs1Element>? ParseBracketed(string text)
    {
        var elements = new List<Gs1Element>();
        var i = 0;
        while (i < text.Length)
        {
            var close = text.IndexOf(')', i);
            if (text[i] != '(' || close < 0)
            {
                return null;
            }

            var ai = text[(i + 1)..close];
            var next = text.IndexOf('(', close);
            var value = next < 0 ? text[(close + 1)..] : text[(close + 1)..next];
            if (!Valid(ai, value))
            {
                return null;
            }

            elements.Add(new Gs1Element(ai, value));
            i = next < 0 ? text.Length : next;
        }

        return elements.Count > 0 ? elements : null;
    }

    private static List<Gs1Element>? ParseRaw(string text)
    {
        var elements = new List<Gs1Element>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == GroupSeparator)
            {
                i++;
                continue;
            }

            if (text.Length - i < 2)
            {
                return null;
            }

            var aiLength = AiLength(text.Substring(i, 2));
            if (text.Length - i < aiLength)
            {
                return null;
            }

            var ai = text.Substring(i, aiLength);
            i += aiLength;
            string value;
            if (FixedLength(ai) is { } length)
            {
                if (text.Length - i < length)
                {
                    return null;
                }

                value = text.Substring(i, length);
                i += length;
            }
            else
            {
                var end = text.IndexOf(GroupSeparator, i);
                value = end < 0 ? text[i..] : text[i..end];
                i = end < 0 ? text.Length : end + 1;
            }

            if (!Valid(ai, value))
            {
                return null;
            }

            elements.Add(new Gs1Element(ai, value));
        }

        return elements.Count > 0 ? elements : null;
    }

    private static bool Valid(string ai, string value)
    {
        if (ai.Length is < 2 or > 4 || !ai.All(char.IsAsciiDigit) || ai.Length != AiLength(ai[..2]) || value.Length == 0)
        {
            return false;
        }

        if (FixedLength(ai) is { } length)
        {
            return value.Length == length && value.All(char.IsAsciiDigit)
                && (ai is not ("00" or "01" or "02") || CheckDigit(value[..^1]) == value[^1] - '0');
        }

        return value.Length <= MaxVariable(ai) && IsCset82(value);
    }

    private static DateOnly? Date(string yymmdd, int thisYear)
    {
        if (!int.TryParse(yymmdd.AsSpan(0, 2), out var yy) || !int.TryParse(yymmdd.AsSpan(2, 2), out var mm) || !int.TryParse(yymmdd.AsSpan(4, 2), out var dd) || mm is < 1 or > 12)
        {
            return null;
        }

        // The century is the one that puts the year within 49 years back and 50 ahead (the General Specifications' rule).
        var year = thisYear / 100 * 100 + yy;
        if (year - thisYear > 50)
        {
            year -= 100;
        }
        else if (thisYear - year > 49)
        {
            year += 100;
        }

        var last = DateTime.DaysInMonth(year, mm);
        return dd == 0 ? new DateOnly(year, mm, last) : dd <= last ? new DateOnly(year, mm, dd) : null;
    }
}
