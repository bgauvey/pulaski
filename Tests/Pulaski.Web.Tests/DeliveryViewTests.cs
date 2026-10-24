using Pulaski.Contracts.Integrations;
using Pulaski.Web.Services.Integrations;

namespace Pulaski.Web.Tests;

/// <summary>How the ERP delivery screen words a delivery (D-061).</summary>
public class DeliveryViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);

    private static DeliveryDto D(string status, int attempts = 0, bool held = false, DateTimeOffset? next = null, string? note = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Sink = "webhook:erp",
        EventId = Guid.CreateVersion7(),
        EventType = "pulaski.lot.written_up.v1",
        Subject = Guid.CreateVersion7(),
        Status = status,
        Attempts = attempts,
        IsHeld = held,
        NextAttemptAt = next ?? Now,
        ResolvedBy = "ops",
        ResolveNote = note,
    };

    [Fact]
    public void A_delivery_says_where_it_stands_and_what_happens_next()
    {
        Assert.Equal(("Failed", "chip chip-danger"), DeliveryView.Status(D("failed")));
        Assert.Equal("Held", DeliveryView.Status(D("pending", held: true)).Text);
        Assert.Equal("Retrying", DeliveryView.Status(D("pending", attempts: 2)).Text);
        Assert.Equal("Queued", DeliveryView.Status(D("pending")).Text);

        Assert.Equal("Next try in 3 min.", DeliveryView.Next(D("pending", attempts: 4, next: Now.AddSeconds(150)), Now));
        Assert.Equal("Next try in 20 s.", DeliveryView.Next(D("pending", attempts: 1, next: Now.AddSeconds(20)), Now));
        Assert.Equal("Skipped by ops: Booked by hand", DeliveryView.Next(D("skipped", note: "Booked by hand"), Now));
        Assert.StartsWith("Waits behind", DeliveryView.Next(D("pending", held: true), Now), StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_type_reads_as_words()
    {
        Assert.Equal("lot written up", DeliveryView.EventName("pulaski.lot.written_up.v1"));
        Assert.Equal("lot received", DeliveryView.EventName("pulaski.lot.received.v2"));
        Assert.Equal("custom", DeliveryView.EventName("custom"));
    }
}
