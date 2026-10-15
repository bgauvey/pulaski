using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

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
        var workOrder = Guid.CreateVersion7();

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
                    wo = workOrder,
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
        Assert.Equal(workOrder, blackJob.WorkOrderId);
        Assert.Empty(timeline.Overlaps);
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
        var (white, black) = await CreateMaterialsAsync();
        await CanRunAsync(line, white);
        var wo = await CreateWorkOrderAsync(site, white);

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

        // No capability for black on this line (D-020).
        var blackOrder = await CreateWorkOrderAsync(site, black);
        var ineligible = await Assert.ThrowsAsync<DomainRuleException>(() => CreateJobAsync(line, blackOrder, "K"));
        Assert.Contains("capability", ineligible.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_stay_in_their_site_and_a_locked_job_stays_put()
    {
        var (site, line) = await CreateLineAsync();
        var (_, otherLine) = await CreateLineAsync();
        var (white, _) = await CreateMaterialsAsync();
        await CanRunAsync(line, white);
        await CanRunAsync(otherLine, white);
        var job = await CreateJobAsync(line, await CreateWorkOrderAsync(site, white), "A");

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
    public async Task Work_order_is_planned_dispatched_firmed_and_a_started_job_cancelled_and_replaced()
    {
        var (site, line) = await CreateLineAsync();
        var (white, _) = await CreateMaterialsAsync();
        await CanRunAsync(line, white);
        await db.ExecuteAsync(
            $"""
            INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
            VALUES ('{line}', '{white}', '{db.UomId}', 1000, 'ideal_rate', now())
            """);
        var wo = await CreateWorkOrderAsync(site, white);
        var first = await CreateJobAsync(line, wo, "A");
        var second = await CreateJobAsync(line, wo, "B");

        // Dispatch publishes both jobs' baselines; the order is 5000 but only 2000 is planned.
        var dispatched = await db.SendAsync(new DispatchWorkOrderCommand(wo, await WorkOrderVersionAsync(wo), "Week 42"));
        Assert.Equal("quantity_mismatch", Assert.Single(dispatched.Warnings).Code);
        var jobs = await JobRowsAsync(line);
        Assert.All(jobs, j => Assert.Equal(JobStatus.Dispatched, j.Status));
        Assert.Equal(jobs[0].End, jobs[1].Start);
        Assert.Equal(TimeSpan.FromHours(1), jobs[1].End - jobs[1].Start);

        // A plain status change can't take it back; un-dispatch can, and clears the baseline.
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ChangeWorkOrderStatusCommand(wo, dispatched.Version, WorkOrderStatus.Planned, null)));
        await db.SendAsync(new UndispatchWorkOrderCommand(wo, dispatched.Version));
        Assert.All(await JobRowsAsync(line), j => Assert.Equal((JobStatus.Planned, (DateTimeOffset?)null), (j.Status, j.Start)));

        dispatched = await db.SendAsync(new DispatchWorkOrderCommand(wo, await WorkOrderVersionAsync(wo)));
        await db.SendAsync(new FirmWorkOrderCommand(wo, dispatched.Version));
        jobs = await JobRowsAsync(line);
        Assert.All(jobs, j => Assert.Equal(JobStatus.Firmed, j.Status));
        Assert.Equal(dispatched.Jobs.Single(j => j.JobId == first).PlannedStart, jobs[0].Start);

        // The first job starts, makes 400 of 1000, and is cancelled and replaced.
        await db.ExecuteAsync(
            $"""
            UPDATE core.work_order SET status = 'in_progress' WHERE id = '{wo}';
            UPDATE core.job SET status = 'running' WHERE id = '{first}';
            INSERT INTO core.job_run (job_id, equipment_id, run_no, started_at, good_qty, uom_id)
            VALUES ('{first}', '{line}', 1, now() - interval '1 hour', 400, '{db.UomId}');
            """);
        var cancelled = await db.SendAsync(new CancelJobCommand(first, await JobVersionAsync(line, first), "Die damage", true));

        var replacement = cancelled.ReplacementJobId!.Value;
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<(string Status, decimal Qty, Guid? Replaces, DateTimeOffset? FirmedAt)>(
            "SELECT status, planned_qty, replaces_job_id, firmed_at FROM core.job WHERE id = @replacement", new { replacement });
        Assert.Equal((JobStatus.Firmed, 600m, (Guid?)first), (row.Status, row.Qty, row.Replaces));
        Assert.NotNull(row.FirmedAt);
        Assert.Equal(JobStatus.Cancelled, await connection.ExecuteScalarAsync<string>("SELECT status FROM core.job WHERE id = @first", new { first }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM core.job_run WHERE job_id = @first AND ended_at IS NULL", new { first }));
        Assert.Equal([replacement, second], (await QueryAsync(line)).Segments.Select(s => s.JobId));

        Assert.Equal(
            [EventTypes.WorkOrderDispatched, EventTypes.WorkOrderUndispatched, EventTypes.WorkOrderDispatched, EventTypes.WorkOrderFirmed],
            await connection.QueryAsync<string>(
                "SELECT type FROM evt.event WHERE subject = @wo AND type <> @created ORDER BY sequence",
                new { wo, created = EventTypes.WorkOrderCreated }));
        Assert.Equal(
            [(null, JobStatus.Planned), (JobStatus.Planned, JobStatus.Dispatched), (JobStatus.Dispatched, JobStatus.Planned),
             (JobStatus.Planned, JobStatus.Dispatched), (JobStatus.Dispatched, JobStatus.Firmed), (JobStatus.Running, JobStatus.Cancelled)],
            await connection.QueryAsync<(string?, string)>(
                "SELECT from_status, to_status FROM ts.job_status_change WHERE job_id = @first ORDER BY occurred_at, id", new { first }));
    }

    [Fact]
    public async Task Firmed_job_moves_to_another_eligible_line_and_keeps_its_time()
    {
        var (site, line) = await CreateLineAsync();
        var other = await CreateLineInSiteAsync(site);
        var spare = await CreateLineInSiteAsync(site);
        var (white, _) = await CreateMaterialsAsync();
        await CanRunAsync(line, white);
        await CanRunAsync(other, white);
        var wo = await CreateWorkOrderAsync(site, white);
        var job = await CreateJobAsync(line, wo, "A");
        var dispatched = await db.SendAsync(new DispatchWorkOrderCommand(wo, await WorkOrderVersionAsync(wo)));
        await db.SendAsync(new FirmWorkOrderCommand(wo, dispatched.Version));
        var anchor = dispatched.Jobs.Single().PlannedStart;

        var version = await JobVersionAsync(line, job);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new SequenceJobCommand(job, version, spare, null)));
        await db.SendAsync(new SequenceJobCommand(job, version, other, null));

        var segment = Assert.Single((await QueryAsync(other)).Segments);
        Assert.Equal((job, JobStatus.Firmed, (DateTimeOffset?)anchor), (segment.JobId, segment.Status, segment.BaselineStart));
        Assert.Empty(await JobRowsAsync(line));
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

    [Fact]
    public async Task Placement_preview_projects_the_job_from_the_database_and_saves_nothing()
    {
        var (site, line) = await CreateLineAsync();
        var spare = await CreateLineInSiteAsync(site);
        var (white, _) = await CreateMaterialsAsync();
        await CanRunAsync(line, white);
        await db.ExecuteAsync(
            $"""
            INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
            VALUES ('{line}', '{white}', '{db.UomId}', 1000, 'ideal_rate', now())
            """);
        var queued = await CreateJobAsync(line, await CreateWorkOrderAsync(site, white), "A");
        var wo = await CreateWorkOrderAsync(site, white);

        await using var scope = db.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<PreviewJobPlacementQuery, JobPlacementPreviewDto>>();
        var preview = await handler.HandleAsync(new PreviewJobPlacementQuery(line, wo, 500m, queued), CancellationToken.None);

        var mine = Assert.Single(preview.Timeline.Segments, s => s.JobId == preview.PreviewJobId);
        Assert.Equal((wo, (decimal?)500m, TimeSpan.FromMinutes(30)), (mine.WorkOrderId, mine.PlannedQty, mine.End - mine.Start));
        Assert.Equal((queued, TimeSpan.FromMinutes(30)), (Assert.Single(preview.Delayed).JobId, preview.Delayed[0].Delay));
        Assert.Equal([queued], (await JobRowsAsync(line)).Select(j => j.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            handler.HandleAsync(new PreviewJobPlacementQuery(spare, wo, 500m, null), CancellationToken.None));
    }

    private async Task<Guid> CreateWorkOrderAsync(Guid site, Guid material, string status = WorkOrderStatus.Planned)
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

    /// <summary>Lets the line run the material from now on (D-020).</summary>
    private Task CanRunAsync(Guid line, Guid material) =>
        db.ExecuteAsync(
            $"""
            INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
            VALUES ('{line}', '{material}', 1000, '{db.UomId}', now() - interval '1 day')
            """);

    private async Task<Guid> CreateLineInSiteAsync(Guid site) =>
        (await db.SendAsync(new CreateEquipmentCommand(
            site, "LINE-" + Guid.NewGuid().ToString("N")[..6], "Line", null, "work_center", "production_line", true, true, false, null))).Id;

    private async Task<long> WorkOrderVersionAsync(Guid workOrder)
    {
        await using var scope = db.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IWorkOrderReadStore>().GetAsync(workOrder, CancellationToken.None))!.Version;
    }

    private async Task<List<(Guid Id, string Status, DateTimeOffset? Start, DateTimeOffset? End)>> JobRowsAsync(Guid line)
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<(Guid, string, DateTimeOffset?, DateTimeOffset?)>(
            """
            SELECT id, status, planned_start, planned_end FROM core.job
            WHERE equipment_id = @line AND status <> 'cancelled'
            ORDER BY sequence_rank, job_number
            """,
            new { line });
        return [.. rows];
    }

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
