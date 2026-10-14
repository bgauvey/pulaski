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

/// <summary>Scheduling end to end: real SQL, as the application login, through the handlers.</summary>
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

    [Fact]
    public async Task Jobs_are_created_moved_and_renumbered_in_rank_order()
    {
        var (site, line) = await CreateLineAsync();
        var (white, _) = await CreateMaterialsAsync();
        var wo = await CreateWorkOrderAsync(site, white, "released");

        var a = await CreateJobAsync(line, wo, "A");
        var b = await CreateJobAsync(line, wo, "B");
        var c = await CreateJobAsync(line, wo, "C");
        Assert.Equal([(a, 1m), (b, 2m), (c, 3m)], await RanksAsync(line));

        // C to the front, then B before A once the ranks have run out of room.
        await db.SendAsync(new SequenceJobCommand(c, await JobVersionAsync(line, c), line, a));
        Assert.Equal([(c, 0m), (a, 1m), (b, 2m)], await RanksAsync(line));

        await db.ExecuteAsync($"UPDATE core.job SET sequence_rank = 1.0000000001 WHERE id = '{c}'; UPDATE core.job SET sequence_rank = 1.0000000002 WHERE id = '{a}'");
        await db.SendAsync(new SequenceJobCommand(b, await JobVersionAsync(line, b), line, a));
        Assert.Equal([(c, 1m), (b, 1.5m), (a, 2m)], await RanksAsync(line));

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ts.job_status_change WHERE job_id = ANY(@ids) AND from_status IS NULL AND to_status = 'planned'",
            new { ids = new[] { a, b, c } }));
        Assert.Equal(
            [EventTypes.JobCreated, EventTypes.JobSequenced],
            await connection.QueryAsync<string>("SELECT type FROM evt.event WHERE subject = @b ORDER BY sequence", new { b }));

        var duplicate = await Assert.ThrowsAsync<ConflictException>(() => CreateJobAsync(line, wo, "A"));
        Assert.Contains("job number", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_stay_in_their_site_and_a_locked_job_stays_put()
    {
        var (site, line) = await CreateLineAsync();
        var (_, otherLine) = await CreateLineAsync();
        var (white, _) = await CreateMaterialsAsync();
        var wo = await CreateWorkOrderAsync(site, white, "released");
        var job = await CreateJobAsync(line, wo, "A");

        var version = await JobVersionAsync(line, job);
        var otherSite = await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new SequenceJobCommand(job, version, otherLine, null)));
        Assert.Contains("another site", otherSite.Message, StringComparison.Ordinal);

        await db.SendAsync(new PinJobCommand(job, version, true, DateTimeOffset.UtcNow.AddDays(1)));
        version = await JobVersionAsync(line, job);
        var locked = await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new SequenceJobCommand(job, version, line, null)));
        Assert.Contains("locked", locked.Message, StringComparison.Ordinal);

        var segment = Assert.Single((await QueryAsync(line)).Segments);
        Assert.True(segment.IsLocked);
        Assert.True(segment.Start > DateTimeOffset.UtcNow.AddHours(23));
    }

    [Fact]
    public async Task Dispatch_materializes_the_changeover_and_publish_writes_the_baseline()
    {
        var (site, line) = await CreateLineAsync();
        var (white, black) = await CreateMaterialsAsync();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
                VALUES (@line, @white, @uom, 1000, 'ideal_rate', now()), (@line, @black, @uom, 1000, 'ideal_rate', now());
                INSERT INTO core.changeover_rule (site_id, from_material_id, to_material_id, duration)
                VALUES (@site, @white, @black, interval '45 minutes');
                """,
                new { site, line, white, black, uom = db.UomId });
        }

        var whiteJob = await CreateJobAsync(line, await CreateWorkOrderAsync(site, white, "released"), "W");
        var blackJob = await CreateJobAsync(line, await CreateWorkOrderAsync(site, black, "released"), "K");

        var dispatched = await db.SendAsync(new DispatchJobCommand(blackJob, await JobVersionAsync(line, blackJob)));

        var changeover = dispatched.ChangeoverJobId!.Value;
        var timeline = await QueryAsync(line);
        Assert.Equal(
            [(SegmentKind.Job, whiteJob), (SegmentKind.Job, changeover), (SegmentKind.Job, blackJob)],
            timeline.Segments.Select(s => (s.Kind, s.JobId)));
        Assert.Equal((JobType.Changeover, JobStatus.Dispatched), (timeline.Segments[1].JobType, timeline.Segments[1].Status));
        Assert.Equal(TimeSpan.FromMinutes(45), timeline.Segments[1].Work);
        Assert.Equal(dispatched.Version, timeline.Segments[2].Version);

        var published = await db.SendAsync(new PublishScheduleCommand(line));

        Assert.Equal([whiteJob, changeover, blackJob], published.Jobs.Select(j => j.JobId));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            var rows = (await connection.QueryAsync<(Guid Id, string Status, DateTimeOffset? Start, DateTimeOffset? End)>(
                "SELECT id, status, planned_start, planned_end FROM core.job WHERE equipment_id = @line ORDER BY sequence_rank",
                new { line })).ToList();
            Assert.Equal([JobStatus.Scheduled, JobStatus.Dispatched, JobStatus.Dispatched], rows.Select(r => r.Status));
            Assert.All(rows, r => Assert.True(r.End > r.Start));
            Assert.Equal(rows[0].End, rows[1].Start);
            Assert.Equal(TimeSpan.FromMinutes(45), rows[1].End - rows[1].Start);

            var history = await connection.QueryAsync<(string? From, string To)>(
                "SELECT from_status, to_status FROM ts.job_status_change WHERE job_id = @blackJob ORDER BY occurred_at, to_status",
                new { blackJob });
            Assert.Equal([(null, JobStatus.Planned), (JobStatus.Planned, JobStatus.Dispatched)], history);

            Assert.Equal(
                [EventTypes.EquipmentCreated, EventTypes.SchedulePublished],
                await connection.QueryAsync<string>("SELECT type FROM evt.event WHERE subject = @line ORDER BY sequence", new { line }));
        }

        var baseline = (await QueryAsync(line)).Segments[0];
        Assert.Equal(published.Jobs[0].PlannedStart, baseline.BaselineStart);
        Assert.Equal(published.Jobs[0].Version, baseline.Version);
    }

    [Fact]
    public async Task Dispatch_refuses_a_job_whose_work_order_is_not_released()
    {
        var (site, line) = await CreateLineAsync();
        var (white, _) = await CreateMaterialsAsync();
        var job = await CreateJobAsync(line, await CreateWorkOrderAsync(site, white, "planned"), "A");
        var version = await JobVersionAsync(line, job);

        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new DispatchJobCommand(job, version)));
        Assert.Equal(JobStatus.Planned, Assert.Single((await QueryAsync(line)).Segments).Status);
    }

    [Fact]
    public async Task Run_standard_modify_and_clear_round_trip()
    {
        var (_, line) = await CreateLineAsync();
        var (white, _) = await CreateMaterialsAsync();
        await db.ExecuteAsync(
            $"""
            INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
            VALUES ('{line}', '{white}', '{db.UomId}', 900, 'ideal_rate', now())
            """);
        var standard = Assert.Single(await ListRunStandardsAsync(line));

        var modified = await db.SendAsync(new ModifyRunStandardCommand(standard.Id, standard.Version, 750m, "Worn screen"));

        var row = Assert.Single(await ListRunStandardsAsync(line));
        Assert.Equal((750m, 900m, RateSource.Modified, "test-user", modified.Version), (row.EffectiveRatePerHour, row.CalculatedRatePerHour, row.RateSource, row.ModifiedBy!, row.Version));
        await Assert.ThrowsAsync<ConflictException>(() =>
            db.SendAsync(new ClearRunStandardCommand(standard.Id, standard.Version, null)));

        await db.SendAsync(new ClearRunStandardCommand(standard.Id, modified.Version, "Screen replaced"));

        row = Assert.Single(await ListRunStandardsAsync(line));
        Assert.Equal((900m, RateSource.Calculated), (row.EffectiveRatePerHour, row.RateSource));
        Assert.Null(row.ModifiedBy);
    }

    [Fact]
    public async Task Policy_starts_as_the_default_and_switches_the_line_to_scheduler_mode()
    {
        var (site, line) = await CreateLineAsync();

        var policy = await GetPolicyAsync(site);
        Assert.Equal((ChangeoverMode.Auto, true, 0L), (policy.ChangeoverMode, policy.IsDefault, policy.Version));

        var set = await db.SendAsync(new SetSchedulingPolicyCommand(site, 0, ChangeoverMode.Scheduler));
        await Assert.ThrowsAsync<ConflictException>(() =>
            db.SendAsync(new SetSchedulingPolicyCommand(site, 0, ChangeoverMode.Auto)));

        policy = await GetPolicyAsync(site);
        Assert.Equal((ChangeoverMode.Scheduler, false, set.Version), (policy.ChangeoverMode, policy.IsDefault, policy.Version));
        Assert.Equal(ChangeoverMode.Scheduler, (await QueryAsync(line)).ChangeoverMode);

        await db.SendAsync(new SetSchedulingPolicyCommand(site, set.Version, ChangeoverMode.Auto));
        Assert.Equal(ChangeoverMode.Auto, (await GetPolicyAsync(site)).ChangeoverMode);
    }

    private async Task<Guid> CreateWorkOrderAsync(Guid site, Guid material, string status)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, status)
            VALUES (@id, @site, @number, @material, 5000, @uom, @status)
            """,
            new { id, site, number = "WO-" + Guid.NewGuid().ToString("N")[..8], material, uom = db.UomId, status });
        return id;
    }

    private async Task<Guid> CreateJobAsync(Guid line, Guid workOrder, string suffix) =>
        (await db.SendAsync(new CreateJobCommand(
            line, $"J-{line.ToString("N")[^6..]}-{suffix}", JobType.Production, workOrder, 1000m, null, false, null, null))).Id;

    private async Task<long> JobVersionAsync(Guid line, Guid job) =>
        (await QueryAsync(line)).Segments.Single(s => s.JobId == job && s.Kind == SegmentKind.Job).Version!.Value;

    private async Task<List<(Guid Id, decimal Rank)>> RanksAsync(Guid line)
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<(Guid, decimal)>(
            "SELECT id, sequence_rank FROM core.job WHERE equipment_id = @line ORDER BY sequence_rank, job_number", new { line });
        return [.. rows];
    }

    private async Task<IReadOnlyList<RunStandardDto>> ListRunStandardsAsync(Guid line)
    {
        await using var scope = db.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<ListRunStandardsQuery, IReadOnlyList<RunStandardDto>>>();
        return await handler.HandleAsync(new ListRunStandardsQuery(line), CancellationToken.None);
    }

    private async Task<SchedulingPolicyDto> GetPolicyAsync(Guid site)
    {
        await using var scope = db.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetSchedulingPolicyQuery, SchedulingPolicyDto>>();
        return await handler.HandleAsync(new GetSchedulingPolicyQuery(site), CancellationToken.None);
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
