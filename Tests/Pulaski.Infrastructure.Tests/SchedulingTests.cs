using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Infrastructure.Tests;

/// <summary>The line timeline read end to end: real SQL, as the application login, through the handler.</summary>
[Collection(DatabaseTests.Name)]
public sealed class SchedulingTests(DatabaseFixture db)
{
    [Fact]
    public async Task Timeline_projects_running_job_changeover_and_maintenance_from_the_database()
    {
        var (site, line) = await CreateLineAsync();
        var (white, black) = await CreateMaterialsAsync();
        var running = Guid.CreateVersion7();
        var next = Guid.CreateVersion7();
        var maintenance = Guid.CreateVersion7();

        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
                VALUES (@line, @white, @uom, 1000, 'ideal_rate', now()),
                       (@line, @black, @uom, 2000, 'ideal_rate', now());
                UPDATE core.run_standard
                SET modified_rate_per_hour = 500, modified_at = now(), modified_by = 'planner-1'
                WHERE equipment_id = @line AND material_id = @black;

                INSERT INTO core.changeover_rule (site_id, from_material_id, to_material_id, duration)
                VALUES (@site, @white, @black, interval '45 minutes');

                INSERT INTO core.shift_instance (scope_equipment_id, production_date, starts_at, ends_at)
                VALUES (@site, current_date, now() - interval '1 hour', now() + interval '30 days');

                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, due_at)
                VALUES (@wo, @site, @woNumber, @black, 1000, @uom, now() + interval '1 hour');

                INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id,
                                      planned_qty, uom_id, sequence_rank, status)
                VALUES (@running, @site, @wo, @running::text, 'production', @line, @white, 2000, @uom, 20, 'running'),
                       (@next,    @site, @wo, @next::text,    'production', @line, @black, 1000, @uom, 10, 'planned');
                INSERT INTO core.job (id, site_id, job_number, job_type, equipment_id, planned_duration, sequence_rank, is_locked)
                VALUES (@maintenance, @site, @maintenance::text, 'maintenance', @line, interval '2 hours', 30, true);

                INSERT INTO core.job_run (job_id, equipment_id, run_no, started_at, good_qty, uom_id)
                VALUES (@running, @line, 1, now() - interval '3 hours', 1500, @uom);
                """,
                new
                {
                    site,
                    line,
                    white,
                    black,
                    uom = db.UomId,
                    running,
                    next,
                    maintenance,
                    wo = Guid.CreateVersion7(),
                    woNumber = "WO-" + Guid.NewGuid().ToString("N")[..8],
                });
        }

        var timeline = await QueryAsync(line);

        Assert.True(timeline.HasShiftCalendar);
        Assert.Equal(ChangeoverMode.Auto, timeline.ChangeoverMode);
        Assert.Empty(timeline.Warnings);
        Assert.Equal(
            [(SegmentKind.Job, running), (SegmentKind.ChangeoverPlaceholder, next), (SegmentKind.Job, next), (SegmentKind.Job, maintenance)],
            timeline.Segments.Select(s => (s.Kind, s.JobId)));

        // Running job: 500 m left at 1000 m/h, from its actual start three hours ago.
        var first = timeline.Segments[0];
        Assert.Equal(500m, first.RemainingQty);
        Assert.Equal(TimeSpan.FromMinutes(30), first.Work);
        Assert.True(first.Start < timeline.ProjectedAt.AddHours(-2.9));

        var placeholder = timeline.Segments[1];
        Assert.Equal(TimeSpan.FromMinutes(45), placeholder.End - placeholder.Start);
        Assert.Equal(first.End, placeholder.Start);
        Assert.Equal(ChangeoverSource.Rule, placeholder.ChangeoverSource);
        Assert.NotNull(placeholder.FromMaterialCode);

        // The modified 500 m/h beats the calculated 2000 m/h: 1000 m takes 2 h, so it misses the due date.
        var blackJob = timeline.Segments[2];
        Assert.Equal((500m, RateSource.Modified), (blackJob.RatePerHour, blackJob.RateSource));
        Assert.Equal(TimeSpan.FromHours(2), blackJob.Work);
        Assert.Equal(ScheduleWarningCode.Late, Assert.Single(blackJob.Warnings).Code);

        var last = timeline.Segments[3];
        Assert.True(last.IsLocked);
        Assert.Equal(TimeSpan.FromHours(2), last.End - last.Start);
    }

    [Fact]
    public async Task Scheduler_policy_and_exceptions_come_from_the_database()
    {
        var (site, line) = await CreateLineAsync();
        var (white, black) = await CreateMaterialsAsync();

        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.scheduling_policy (site_id, changeover_mode) VALUES (@site, 'scheduler');
                INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
                VALUES (@line, @white, @uom, 1000, 'ideal_rate', now()), (@line, @black, @uom, 1000, 'ideal_rate', now());
                INSERT INTO core.changeover_rule (site_id, equipment_id, duration) VALUES (@site, @line, interval '1 hour');
                INSERT INTO core.calendar_exception (scope_equipment_id, kind, starts_at, ends_at)
                VALUES (@site, 'planned_shutdown', now() + interval '10 minutes', now() + interval '70 minutes');
                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id)
                VALUES (@wo, @site, @woNumber, @white, 2000, @uom);
                INSERT INTO core.job (site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, sequence_rank)
                VALUES (@site, @wo, @j1, 'production', @line, @white, 1000, @uom, 1),
                       (@site, @wo, @j2, 'production', @line, @black, 1000, @uom, 2);
                """,
                new
                {
                    site,
                    line,
                    white,
                    black,
                    uom = db.UomId,
                    wo = Guid.CreateVersion7(),
                    woNumber = "WO-" + Guid.NewGuid().ToString("N")[..8],
                    j1 = "J-" + Guid.NewGuid().ToString("N")[..8],
                    j2 = "J-" + Guid.NewGuid().ToString("N")[..8],
                });
        }

        var timeline = await QueryAsync(line);

        Assert.Equal(ChangeoverMode.Scheduler, timeline.ChangeoverMode);
        Assert.False(timeline.HasShiftCalendar);
        Assert.Equal(ScheduleWarningCode.NoShiftCalendar, Assert.Single(timeline.Warnings).Code);
        Assert.All(timeline.Segments, s => Assert.Equal(SegmentKind.Job, s.Kind));

        // 1 h of work around a 1 h shutdown takes 2 h of wall clock.
        var first = timeline.Segments[0];
        Assert.Equal(TimeSpan.FromHours(2), first.End - first.Start);
        Assert.Contains(timeline.Segments[1].Warnings, w => w.Code == ScheduleWarningCode.MissingChangeover);
    }

    [Fact]
    public async Task Unknown_or_unschedulable_equipment_is_refused()
    {
        var (site, _) = await CreateLineAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => QueryAsync(Guid.CreateVersion7()));
        await Assert.ThrowsAsync<DomainRuleException>(() => QueryAsync(site));
    }

    private async Task<LineTimelineDto> QueryAsync(Guid line)
    {
        await using var scope = db.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetLineTimelineQuery, LineTimelineDto>>();
        return await handler.HandleAsync(new GetLineTimelineQuery(line), CancellationToken.None);
    }

    private async Task<(Guid Site, Guid Line)> CreateLineAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(
            null, "ENT-" + suffix, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(
            enterprise.Id, "SITE-" + suffix, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(
            site.Id, "LINE-" + suffix, "Line", null, "work_center", "production_line", true, true, false, null));
        return (site.Id, line.Id);
    }

    private async Task<(Guid White, Guid Black)> CreateMaterialsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var white = Guid.CreateVersion7();
        var black = Guid.CreateVersion7();
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.material (id, code, name, material_type, base_uom_id)
            VALUES (@white, @whiteCode, 'White profile', 'finished', @uom),
                   (@black, @blackCode, 'Black profile', 'finished', @uom);
            """,
            new { white, black, whiteCode = "WH-" + suffix, blackCode = "BK-" + suffix, uom = db.UomId });
        return (white, black);
    }
}
