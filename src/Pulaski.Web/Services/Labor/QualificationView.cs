using System.Globalization;
using Pulaski.Contracts.Labor;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Labor;

/// <summary>How the People and Equipment screens describe qualifications and grants (D-048).</summary>
public static class QualificationView
{
    public static string StatusLabel(Text l, string status) => status switch
    {
        "valid" => l["Valid"],
        "not_yet" => l["Not valid yet"],
        "expired" => l["Expired"],
        "revoked" => l["Revoked"],
        _ => status,
    };

    public static string StatusChip(string status) => status switch
    {
        "valid" => "chip",
        "expired" or "revoked" => "chip chip-warning",
        _ => "chip chip-muted",
    };

    /// <summary>"12 Oct 2026 – 11 Oct 2027", or "from 12 Oct 2026, no expiry".</summary>
    public static string Days(Text l, QualificationGrantDto grant) =>
        grant.ExpiresOn is { } expires
            ? $"{Day(grant.ValidFrom)} – {Day(expires)}"
            : l["from {0}, no expiry", Day(grant.ValidFrom)];

    /// <summary>"Expires in 12 days", "Expired 3 days ago"; null when it doesn't expire or is far off.</summary>
    public static string? Countdown(Text l, QualificationGrantDto grant, DateOnly today)
    {
        if (grant.ExpiresOn is not { } expires || grant.Status == "revoked")
        {
            return null;
        }

        var days = expires.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => days == -1 ? l["Expired 1 day ago"] : l["Expired {0} days ago", -days],
            0 => l["Expires today"],
            <= 30 => days == 1 ? l["Expires in 1 day"] : l["Expires in {0} days", days],
            _ => null,
        };
    }

    /// <summary>"12 months", or "No expiry".</summary>
    public static string Validity(Text l, int? months) => months is { } m ? (m == 1 ? l["1 month"] : l["{0} months", m]) : l["No expiry"];

    /// <summary>The last valid day of a new grant from <paramref name="from"/>, for the form to suggest.</summary>
    public static DateOnly? DefaultExpiry(DateOnly from, int? months) => months is > 0 ? from.AddMonths(months.Value).AddDays(-1) : null;

    private static string Day(DateOnly day) => day.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}
