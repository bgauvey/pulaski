using Microsoft.Extensions.Time.Testing;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Scheduling;

public class GetLineTimelineHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly GetLineTimelineHandler _handler;

    public GetLineTimelineHandlerTests() =>
        _handler = new GetLineTimelineHandler(_f.Stores.Scheduling, new FakeTimeProvider(HandlerFixture.Now));

    private static SchedulingLineRow Line(bool schedulable = true, string mode = ChangeoverMode.Auto) =>
        new() { Id = Guid.CreateVersion7(), Code = "EXT-01", Name = "Line 1", IsSchedulable = schedulable, ChangeoverMode = mode };

    [Fact]
    public async Task Maps_rows_to_a_projected_timeline_with_display_fields()
    {
        var white = Guid.CreateVersion7();
        var black = Guid.CreateVersion7();
        var metre = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var source = _f.Stores.Scheduling.Add(new LinePlanSource
        {
            Line = Line(),
            PreviousMaterial = new PlanMaterialRow { MaterialId = white, MaterialCode = "WH" },
            Jobs =
            [
                new PlanJobRow
                {
                    JobId = job, JobNumber = "J-1", JobType = JobType.Production, Status = JobStatus.Planned,
                    MaterialId = black, MaterialCode = "BK", PlannedQty = 1000m, UomId = metre, UomCode = "m",
                    WorkOrderNumber = "WO-1", IsLocked = true,
                    PlannedStart = HandlerFixture.Now, PlannedEnd = HandlerFixture.Now.AddHours(1),
                },
            ],
            Rates = [new RunRateRow { MaterialId = black, UomId = metre, RatePerHour = 500m, RateSource = RateSource.Modified }],
            StandardChangeovers = [new StandardChangeoverRow { MaterialId = black, StandardChangeover = TimeSpan.FromMinutes(30) }],
            HasShiftCalendar = true,
            Shifts = [new WindowRow { StartsAt = HandlerFixture.Now.AddHours(-1), EndsAt = HandlerFixture.Now.AddHours(12) }],
        });

        var timeline = await _handler.HandleAsync(new GetLineTimelineQuery(source.Line.Id), CancellationToken.None);

        Assert.Equal(HandlerFixture.Now, timeline.ProjectedAt);
        Assert.Empty(timeline.Warnings);
        Assert.Collection(
            timeline.Segments,
            co =>
            {
                Assert.Equal((SegmentKind.ChangeoverPlaceholder, job, "J-1"), (co.Kind, co.JobId, co.JobNumber));
                Assert.Equal((JobType.Changeover, "WH", "BK"), (co.JobType, co.FromMaterialCode, co.MaterialCode));
                Assert.Equal(ChangeoverSource.Standard, co.ChangeoverSource);
                Assert.False(co.IsLocked);
                Assert.Null(co.BaselineStart);
            },
            run =>
            {
                Assert.Equal(HandlerFixture.Now.AddMinutes(30), run.Start);
                Assert.Equal(HandlerFixture.Now.AddHours(2.5), run.End);
                Assert.Equal((500m, RateSource.Modified, "m", "WO-1"), (run.RatePerHour, run.RateSource, run.UomCode, run.WorkOrderNumber));
                Assert.Equal(HandlerFixture.Now.AddHours(1), run.BaselineEnd);
                Assert.True(run.IsLocked);
            });
    }

    [Fact]
    public async Task Unknown_line_is_not_found() =>
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _handler.HandleAsync(new GetLineTimelineQuery(Guid.CreateVersion7()), CancellationToken.None));

    [Fact]
    public async Task Unschedulable_equipment_is_a_rule_violation()
    {
        var source = _f.Stores.Scheduling.Add(new LinePlanSource { Line = Line(schedulable: false) });

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            _handler.HandleAsync(new GetLineTimelineQuery(source.Line.Id), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(367)]
    public async Task Horizon_out_of_range_is_invalid(int days)
    {
        var source = _f.Stores.Scheduling.Add(new LinePlanSource { Line = Line() });

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _handler.HandleAsync(new GetLineTimelineQuery(source.Line.Id, days), CancellationToken.None));
        Assert.Contains(nameof(GetLineTimelineQuery.HorizonDays), ex.Errors.Keys);
    }
}
