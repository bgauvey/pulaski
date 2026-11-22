using Pulaski.Contracts.Handover;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>How shift handover reads (#124).</summary>
public class HandoverViewTests
{
    [Fact]
    public void Shifts_and_writers_read_in_the_sites_time()
    {
        var day = new DateOnly(2026, 11, 20);
        Assert.Equal("Fri 20 Nov", HandoverView.Shift(new HandoverShiftDto { ProductionDate = day }, "UTC"));
        Assert.Equal("Afternoons, Fri 20 Nov (08:00–16:00)", HandoverView.Shift(new HandoverShiftDto
        {
            ProductionDate = day,
            ShiftCode = "B",
            ShiftName = "Afternoons",
            Start = new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero),
        }, "America/Chicago"));

        var now = new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);
        var entry = new HandoverEntryDto
        {
            Id = Guid.NewGuid(),
            LineId = Guid.NewGuid(),
            Kind = "note",
            Body = "x",
            WrittenAt = now.AddHours(-1),
            WrittenBy = "lee",
            WrittenByName = "Lee Ong",
        };
        Assert.Equal("Lee Ong at 19:00", HandoverView.Who(entry, "UTC", now));
        Assert.Equal("lee, Wed 18 Nov 19:00", HandoverView.Who(entry with { WrittenByName = null, WrittenAt = now.AddDays(-2).AddHours(-1) }, "UTC", now));
        Assert.Equal(("1 open item", "3 open items"), (HandoverView.OpenItems(1), HandoverView.OpenItems(3)));
    }
}
