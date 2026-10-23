using System.Globalization;
using Pulaski.Contracts.Labor;

namespace Pulaski.Web.Services.Labor;

/// <summary>How the People and Equipment screens describe qualifications and grants (D-048).</summary>
public static class QualificationView
{
    public static string StatusLabel(string status) => status switch
    {
        "valid" => "Valid",
        "not_yet" => "Not valid yet",
        "expired" => "Expired",
        "revoked" => "Revoked",
        _ => status,
    };

    public static string StatusChip(string status) => status switch
    {
        "valid" => "chip",
        "expired" or "revoked" => "chip chip-warning",
        _ => "chip chip-muted",
    };

    /// <summary>"12 Oct 2026 – 11 Oct 2027", or "from 12 Oct 2026, no expiry".</summary>
    public static string Days(QualificationGrantDto grant) =>
        grant.ExpiresOn is { } expires
            ? $"{Day(grant.ValidFrom)} – {Day(expires)}"
            : $"from {Day(grant.ValidFrom)}, no expiry";

    /// <summary>"Expires in 12 days", "Expired 3 days ago"; null when it doesn't expire or is far off.</summary>
    public static string? Countdown(QualificationGrantDto grant, DateOnly today)
    {
        if (grant.ExpiresOn is not { } expires || grant.Status == "revoked")
        {
            return null;
        }

        var days = expires.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => $"Expired {-days} {(days == -1 ? "day" : "days")} ago",
            0 => "Expires today",
            <= 30 => $"Expires in {days} {(days == 1 ? "day" : "days")}",
            _ => null,
        };
    }

    /// <summary>"12 months", or "No expiry".</summary>
    public static string Validity(int? months) => months is { } m ? $"{m} {(m == 1 ? "month" : "months")}" : "No expiry";

    /// <summary>The last valid day of a new grant from <paramref name="from"/>, for the form to suggest.</summary>
    public static DateOnly? DefaultExpiry(DateOnly from, int? months) => months is > 0 ? from.AddMonths(months.Value).AddDays(-1) : null;

    private static string Day(DateOnly day) => day.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
