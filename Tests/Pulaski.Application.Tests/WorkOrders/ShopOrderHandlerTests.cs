using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Boms;
using Pulaski.Contracts.Scheduling;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.WorkOrders;

public sealed class ShopOrderHandlerTests
{
    [Fact]
    public async Task Includes_projected_schedule_rate_components_and_operation_barcode()
    {
        var stores = new InMemoryStores();
        var order = stores.WorkOrders.Add("planned");
        var jobId = Guid.CreateVersion7();
        var lineId = Guid.CreateVersion7();
        var start = new DateTimeOffset(2026, 10, 19, 13, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(2);
        stores.WorkOrders.ShopOrderJobs = id => id == order.Id
            ? new ShopOrderJobsRow
            {
                SiteTimeZone = "America/Chicago",
                Jobs =
                [
                    new ShopOrderJobRow
                    {
                        JobId = jobId,
                        JobNumber = "JOB-10",
                        JobType = "production",
                        Status = "planned",
                        EquipmentId = lineId,
                        EquipmentCode = "EXTRUDER-01",
                        PlannedQty = 100m,
                        UomCode = "kg",
                        OperationNumber = 10,
                        OperationName = "Extrude",
                        OperationDescription = "Run the required profile.",
                        BaselineStart = start.AddDays(1),
                        BaselineEnd = end.AddDays(1),
                        RatePerHour = 80m,
                        RateUomCode = "kg",
                        RateSource = "calculated",
                    },
                ],
            }
            : null;
        stores.Boms.PlannedComponents =
        [
            new PlannedComponentDto
            {
                JobId = jobId,
                JobNumber = "JOB-10",
                EquipmentCode = "EXTRUDER-01",
                OperationNumber = 10,
                LineNo = 1,
                MaterialId = Guid.CreateVersion7(),
                MaterialCode = "RESIN-1",
                MaterialName = "Resin",
                PlannedQty = 25m,
                UomCode = "kg",
                ConsumptionMethod = "declared",
            },
        ];
        var timeline = new FixedTimelineHandler(new LineTimelineDto
        {
            LineId = lineId,
            LineCode = "EXTRUDER-01",
            LineName = "Extruder 1",
            ChangeoverMode = "auto",
            Segments =
            [
                new TimelineSegmentDto
                {
                    Kind = "job",
                    JobId = jobId,
                    JobNumber = "JOB-10",
                    JobType = "production",
                    Status = "planned",
                    Start = start,
                    End = end,
                    RatePerHour = 120m,
                    RateSource = "modified",
                },
            ],
        });
        var handler = new GetShopOrderHandler(stores.WorkOrders, stores.Boms, timeline);

        var result = await handler.HandleAsync(new GetShopOrderQuery(order.Id), CancellationToken.None);
        var job = Assert.Single(result.Jobs);

        Assert.Equal("America/Chicago", result.SiteTimeZone);
        Assert.Equal(start, job.ScheduledStart);
        Assert.True(job.UsesProjectedSchedule);
        Assert.Equal((120m, "kg", "modified"), (job.RatePerHour, job.RateUomCode, job.RateSource));
        Assert.Equal($"{order.OrderNumber}|OP-10", job.BarcodeValue);
        Assert.Equal("Run the required profile.", job.OperationDescription);
        Assert.Equal("RESIN-1", Assert.Single(job.Components).MaterialCode);
    }

    private sealed class FixedTimelineHandler(LineTimelineDto timeline)
        : IQueryHandler<GetLineTimelineQuery, LineTimelineDto>
    {
        public Task<LineTimelineDto> HandleAsync(GetLineTimelineQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(timeline);
    }
}
