using Pulaski.Contracts.Labor;
using Pulaski.Web.Services.Labor;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Tests;

/// <summary>How qualifications and their grants read on People and Equipment (D-048).</summary>
public class QualificationViewTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    private static QualificationGrantDto Grant(DateOnly? expires, string status = "valid") => new()
    {
        Id = Guid.CreateVersion7(),
        PersonId = Guid.CreateVersion7(),
        QualificationId = Guid.CreateVersion7(),
        ValidFrom = new DateOnly(2025, 11, 1),
        ExpiresOn = expires,
        Status = status,
    };

    [Fact]
    public void Grants_read_as_their_days_and_how_soon_they_expire()
    {
        Assert.Equal("1 Nov 2025 – 31 Oct 2026", QualificationView.Days(Text.English, Grant(new DateOnly(2026, 10, 31))));
        Assert.Equal("from 1 Nov 2025, no expiry", QualificationView.Days(Text.English, Grant(null)));
        Assert.Equal("Expires in 8 days", QualificationView.Countdown(Text.English, Grant(new DateOnly(2026, 10, 31)), Today));
        Assert.Equal("Expires today", QualificationView.Countdown(Text.English, Grant(Today), Today));
        Assert.Equal("Expired 1 day ago", QualificationView.Countdown(Text.English, Grant(Today.AddDays(-1), "expired"), Today));
        Assert.Null(QualificationView.Countdown(Text.English, Grant(Today.AddDays(60)), Today));
        Assert.Null(QualificationView.Countdown(Text.English, Grant(Today, "revoked"), Today));
    }

    [Fact]
    public void States_validity_and_the_suggested_expiry_are_worded_for_people()
    {
        Assert.Equal(("Not valid yet", "chip chip-warning"), (QualificationView.StatusLabel(Text.English, "not_yet"), QualificationView.StatusChip("expired")));
        Assert.Equal(("12 months", "1 month", "No expiry"), (QualificationView.Validity(Text.English, 12), QualificationView.Validity(Text.English, 1), QualificationView.Validity(Text.English, null)));
        Assert.Equal(new DateOnly(2027, 10, 22), QualificationView.DefaultExpiry(Today, 12));
        Assert.Null(QualificationView.DefaultExpiry(Today, null));
    }
}
