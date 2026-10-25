using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Materials;
using Pulaski.Application.Scheduling;
using Pulaski.Application.Units;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Execution;
using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Execution;

/// <summary>
/// Starts or resumes a job on its line, opening a run (D-035). A job waiting on a department needs
/// <paramref name="LineId"/>. Where the material changes on a line in automatic changeover mode, a
/// changeover job starts first unless <paramref name="SkipChangeover"/> (D-015).
/// </summary>
public sealed record StartJobCommand(Guid JobId, long ExpectedVersion, Guid? LineId = null, bool SkipChangeover = false)
    : ICommand<ExecutionResponse>;

/// <summary>
/// Stops a running job for now; its run ends with an optional reason of kind <c>run_end</c>. When the line
/// stopped in the middle of a batch, <paramref name="PartialBatch"/> reports it, and needs the reason (D-036).
/// </summary>
public sealed record PauseJobCommand(
    Guid JobId, long ExpectedVersion, Guid? ReasonCodeId = null, string? Comment = null, PartialBatchInput? PartialBatch = null)
    : ICommand<ExecutionResponse>;

/// <summary>Finishes a running or paused job; its work order completes with its last open job. A job cut short may report its partial batch, with a reason.</summary>
public sealed record CompleteJobCommand(
    Guid JobId, long ExpectedVersion, Guid? ReasonCodeId = null, string? Comment = null, PartialBatchInput? PartialBatch = null)
    : ICommand<ExecutionResponse>;

/// <summary>The batch in progress when a line stopped (D-036): its quantity in the material's base unit, and where it goes.</summary>
public sealed record PartialBatchInput(decimal Quantity, Guid LocationId, string? LotNumber);

/// <summary>Takes a completed job back to paused, and its work order back to in progress.</summary>
public sealed record ReopenJobCommand(Guid JobId, long ExpectedVersion) : ICommand<ExecutionResponse>;

/// <summary>Sets a site's tolerances for production reporting (D-035).</summary>
public sealed record SetExecutionPolicyCommand(Guid SiteId, long ExpectedVersion, decimal RateTolerancePct, decimal ConsumptionTolerancePct) : ICommand<ExecutionPolicyDto>;

/// <summary>A job locked for an execution change, with its work order and the order's open jobs (D-017 order: lines, order, jobs).</summary>
internal sealed record LockedJob(
    Versioned<Job> Job, IReadOnlyList<SchedulingLine> Lines, WorkOrder? WorkOrder, long WorkOrderVersion, IReadOnlyList<Job> OrderJobs);

/// <summary>Locks, readiness and notes shared by the execution commands.</summary>
internal sealed class ExecutionWrites(
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events)
{
    /// <summary>
    /// Locks the lines first, then the work order, then the jobs, as every scheduling write does (D-017).
    /// For a job of an order, all the order's lines are locked, because a start may firm the order and a
    /// completion may complete it.
    /// </summary>
    public async Task<LockedJob> LockAsync(Guid jobId, long expectedVersion, Guid? otherLineId, bool withOrder, CancellationToken cancellationToken)
    {
        var lineId = await scheduling.GetJobLineAsync(jobId, cancellationToken) ?? throw new NotFoundException($"Job {jobId} not found.");
        var workOrderId = withOrder ? await execution.GetJobWorkOrderAsync(jobId, cancellationToken) : null;
        var lineIds = new HashSet<Guid> { lineId };
        if (otherLineId is { } other)
        {
            lineIds.Add(other);
        }

        if (workOrderId is { } orderId)
        {
            lineIds.UnionWith(await scheduling.GetWorkOrderLinesAsync(orderId, cancellationToken));
        }

        var lines = await scheduling.LockLinesAsync(lineIds, cancellationToken);
        var workOrder = workOrderId is { } id ? await workOrders.GetForUpdateAsync(id, cancellationToken) : null;
        var job = await scheduling.GetJobForUpdateAsync(jobId, cancellationToken) ?? throw new NotFoundException($"Job {jobId} not found.");
        SchedulingRules.EnsureVersion(job, expectedVersion);
        if (job.Value.EquipmentId != lineId)
        {
            throw new ConflictException($"Job {job.Value.JobNumber} was changed by someone else. Reload and try again.");
        }

        var orderJobs = workOrder is null
            ? []
            : (await scheduling.GetWorkOrderJobsForUpdateAsync(workOrder.Value.Id, cancellationToken)).Select(j => j.Value).ToList();
        return new LockedJob(job, lines, workOrder?.Value, workOrder?.Version ?? 0, orderJobs);
    }

    /// <summary>Saves a job's new status, with its status change row; returns the new version.</summary>
    public async Task<long> SaveJobAsync(Job before, Job after, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await scheduling.UpdateJobAsync(after, at, cancellationToken);
        if (before.Status != after.Status)
        {
            await scheduling.AppendStatusChangeAsync(after.Id, before.Status, after.Status, at, cancellationToken);
        }

        return version;
    }

    /// <summary>The work order's status follows its jobs once execution starts (D-035).</summary>
    public async Task<string?> RollUpAsync(WorkOrder? workOrder, string to, string why, CancellationToken cancellationToken)
    {
        if (workOrder is null || workOrder.Status == to || !WorkOrderStatus.CanTransition(workOrder.Status, to))
        {
            return null;
        }

        var envelope = events.Create(
            EventTypes.WorkOrderStatusChanged, "work_order", workOrder.Id,
            new { Before = new { workOrder.Status }, After = new { Status = to } }, why);
        await workOrders.UpdateStatusAsync(workOrder.Id, to, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return to switch
        {
            WorkOrderStatus.InProgress when workOrder.Status == WorkOrderStatus.Completed => $"Work order {workOrder.OrderNumber} is in progress again.",
            WorkOrderStatus.InProgress => $"Work order {workOrder.OrderNumber} is now in progress.",
            WorkOrderStatus.Completed => $"Work order {workOrder.OrderNumber} is complete: all its jobs are done.",
            _ => null,
        };
    }

    /// <summary>Reports the partial batch of a run that is being ended (D-036); returns the notes for the operator.</summary>
    public static async Task<IReadOnlyList<string>> ReportPartialAsync(
        ProductionReporter reporter, Job job, JobRun run, PartialBatchInput partial, string? comment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await reporter.ReportAsync(
            new ReportProductionCommand(job.Id, run.Id, 0m, 0m, null, null, partial.LocationId, partial.LotNumber, Comment: comment) { PartialQty = partial.Quantity },
            onStop: true, now, cancellationToken);
        return [.. result.Warnings.Select(w => w.Message)];
    }

    /// <summary>Notes on the jobs that wait for this one: which can start now, and which in how long.</summary>
    public async Task<IReadOnlyList<string>> DownstreamNotesAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        foreach (var downstream in await execution.GetDownstreamJobsAsync(jobId, cancellationToken))
        {
            if (!JobStatus.Open.Contains(downstream.Status) || JobStatus.Started.Contains(downstream.Status))
            {
                continue;
            }

            var (links, upstream) = await execution.GetUpstreamAsync(downstream.JobId, cancellationToken);
            var readiness = JobReadiness.For(links, upstream, now);
            if (readiness.IsReady)
            {
                notes.Add($"{downstream.JobNumber} on {downstream.LineCode} can start now.");
            }
            else if (readiness.ReadyAt is { } at)
            {
                notes.Add($"{downstream.JobNumber} on {downstream.LineCode} can start in {RateCap.Duration(at - now)}.");
            }
        }

        return notes;
    }
}

internal sealed class StartJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    Boms.IBomRepository? boms = null,
    Units.IUnitRepository? units = null,
    ILineBatchSizes? lineBatches = null) : ICommandHandler<StartJobCommand, ExecutionResponse>
{
    public async Task<ExecutionResponse> HandleAsync(StartJobCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var writes = new ExecutionWrites(scheduling, workOrders, execution, eventLog, events);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var locked = await writes.LockAsync(command.JobId, command.ExpectedVersion, command.LineId, withOrder: true, cancellationToken);
        var job = locked.Job.Value;
        var notes = new List<string>();

        // A job waiting on a department starts on the line the floor picks (D-022).
        if (job.IsOnGroup)
        {
            job = await AssignAsync(job, locked.Lines, command.LineId, now, cancellationToken);
        }
        else if (command.LineId is { } lineId && lineId != job.EquipmentId)
        {
            throw new DomainRuleException($"Job {job.JobNumber} is planned on another line; move it there on the schedule first.");
        }

        var line = locked.Lines.First(l => l.Id == job.EquipmentId);
        if (!line.CanRunJob)
        {
            throw new DomainRuleException($"{line.Code} doesn't run jobs; choose a line below it.");
        }

        if (await execution.GetRunningJobOnLineAsync(line.Id, job.Id, cancellationToken) is { } running)
        {
            throw new DomainRuleException($"{running} is running on {line.Code}; pause or complete it first.");
        }

        var isProduction = job.JobType == JobType.Production;
        if (isProduction)
        {
            if (locked.WorkOrder is { } order && order.Status is WorkOrderStatus.Planned)
            {
                throw new DomainRuleException($"Work order {order.OrderNumber} is planned; dispatch it before starting its jobs.");
            }

            await SchedulingRules.EnsureEligibleAsync(scheduling, line, job.MaterialId!.Value, now, cancellationToken);
            if (job.Status != JobStatus.Paused && job.ProcessOperationId is not null)
            {
                var (links, upstream) = await execution.GetUpstreamAsync(job.Id, cancellationToken);
                var readiness = JobReadiness.For(links, upstream, now);
                if (!readiness.IsReady)
                {
                    throw new DomainRuleException($"{job.JobNumber} can't start yet. {readiness.Waiting}");
                }
            }

            // In automatic mode the changeover placeholder becomes a real job when the next production job starts (D-015).
            if (!command.SkipChangeover && line.ChangeoverMode == ChangeoverMode.Auto && job.Status != JobStatus.Paused
                && await execution.GetLastRunJobAsync(line.Id, cancellationToken) is { JobType: JobType.Production } last
                && last.MaterialId != job.MaterialId)
            {
                var changeover = await StartChangeoverAsync(job, line, now, cancellationToken);
                notes.Add($"The material changes from {last.MaterialCode} on {line.Code}, so changeover {changeover.Job.JobNumber} started first. " +
                    $"Complete it when the line is ready, then start {job.JobNumber}; if no changeover is needed, complete it straight away.");
                await transaction.CommitAsync(cancellationToken);
                return new ExecutionResponse(changeover.Job.Id, changeover.Version, changeover.RunId, notes);
            }

            job = await FirmOrderAsync(locked, job, now, cancellationToken);
        }

        var run = JobRun.Open(ids.NewId(), job.Id, line.Id, await execution.NextRunNoAsync(job.Id, cancellationToken), now, job.UomId);
        var started = job.Start();
        var envelope = events.Create(
            EventTypes.JobStarted, "job", job.Id,
            new { Before = new { job.Status }, After = new { started.Status }, RunId = run.Id, run.RunNo, LineId = line.Id });
        await execution.InsertRunAsync(run, cancellationToken);
        var version = await writes.SaveJobAsync(job, started, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        if (isProduction && locked.WorkOrder is { } wo)
        {
            // A dispatched order was firmed above, so it moves on from firmed.
            var current = wo.Status == WorkOrderStatus.Dispatched ? wo with { Status = WorkOrderStatus.Firmed } : wo;
            notes.AddIfSet(await writes.RollUpAsync(current, WorkOrderStatus.InProgress, "Its first job started.", cancellationToken));
        }

        notes.AddRange(await writes.DownstreamNotesAsync(job.Id, now, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new ExecutionResponse(job.Id, version, run.Id, notes);
    }

    private async Task<Job> AssignAsync(Job job, IReadOnlyList<SchedulingLine> lines, Guid? lineId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (lineId is not { } target)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["LineId"] = [$"Job {job.JobNumber} is waiting on its department; choose the line it runs on."],
            });
        }

        var line = lines.FirstOrDefault(l => l.Id == target) ?? throw new DomainRuleException($"Line {target} not found.");
        if (!line.CanRunJob || !await scheduling.IsUnderAsync(job.GroupEquipmentId!.Value, target, cancellationToken))
        {
            throw new DomainRuleException($"{line.Code} isn't a line of the department {job.JobNumber} was planned to.");
        }

        await SchedulingRules.EnsureEligibleAsync(scheduling, line, job.MaterialId!.Value, now, cancellationToken);

        // The line it starts on takes it only in whole batches of its own size (D-065).
        if (boms is not null && units is not null && job.PlannedQty is { } quantity && job.UomId is { } uomId && !job.PartialBatchPlanned)
        {
            await BatchPlanning.EnsureWholeAsync(boms, units, job.MaterialId!.Value, quantity, uomId, $"Job {job.JobNumber} on {line.Code}", lineBatches, line.Id, now, cancellationToken);
        }

        var assigned = job.Assign(target, targetIsGroup: false, await SchedulingRules.RankAsync(scheduling, target, null, job.Id, now, cancellationToken));
        var envelope = events.Create(
            EventTypes.JobAssigned, "job", job.Id,
            new { Before = new { job.EquipmentId }, After = new { assigned.EquipmentId }, OnStart = true });
        await scheduling.UpdateJobAsync(assigned, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return assigned;
    }

    /// <summary>Starting firms a dispatched order and its jobs first (D-035): execution freezes the plan.</summary>
    private async Task<Job> FirmOrderAsync(LockedJob locked, Job job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (locked.WorkOrder is not { Status: WorkOrderStatus.Dispatched } order)
        {
            return job;
        }

        var jobs = locked.OrderJobs.Select(j => j.Id == job.Id ? job : j).ToList();
        var firmed = jobs.Select(j => j.Status == JobStatus.Dispatched ? j.Firm(now) : j).ToList();
        await WorkOrderPlanning.SaveAsync(
            scheduling, workOrders, eventLog, events, EventTypes.WorkOrderFirmed, "Its first job started.",
            order, order.WithStatus(WorkOrderStatus.Firmed), jobs, firmed, cancellationToken);
        return firmed.First(j => j.Id == job.Id);
    }

    private async Task<(Job Job, long Version, Guid RunId)> StartChangeoverAsync(Job next, SchedulingLine line, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var number = next.JobNumber + "-CO";
        for (var n = 2; await execution.JobNumberExistsAsync(next.SiteId, number, cancellationToken); n++)
        {
            number = $"{next.JobNumber}-CO{n}";
        }

        var rank = await SchedulingRules.RankAsync(scheduling, line.Id, next.Id, null, now, cancellationToken);
        var changeover = Job.CreateActivity(ids.NewId(), next.SiteId, JobType.Changeover, number, line.Id, null, false, rank, null);
        var created = events.Create(EventTypes.JobCreated, "job", changeover.Id, new { After = changeover, ForJobId = next.Id });
        await scheduling.InsertJobAsync(changeover, created.OccurredAt, cancellationToken);
        await scheduling.AppendStatusChangeAsync(changeover.Id, null, changeover.Status, created.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(created, cancellationToken);

        var run = JobRun.Open(ids.NewId(), changeover.Id, line.Id, 1, now, null);
        var started = changeover.Start();
        var envelope = events.Create(
            EventTypes.JobStarted, "job", changeover.Id,
            new { Before = new { changeover.Status }, After = new { started.Status }, RunId = run.Id, run.RunNo, LineId = line.Id });
        await execution.InsertRunAsync(run, cancellationToken);
        var version = await scheduling.UpdateJobAsync(started, envelope.OccurredAt, cancellationToken);
        await scheduling.AppendStatusChangeAsync(changeover.Id, changeover.Status, started.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return (started, version, run.Id);
    }
}

internal sealed class PauseJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IExecutionRepository execution,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILineBatchSizes? lineBatches = null) : ICommandHandler<PauseJobCommand, ExecutionResponse>
{
    public async Task<ExecutionResponse> HandleAsync(PauseJobCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .RequireComment(command.Comment)
            .Require(command.PartialBatch is null || command.ReasonCodeId is not null, nameof(command.ReasonCodeId), "Say why the line stopped: a partial batch is an exception.")
            .ThrowIfInvalid();
        var now = timeProvider.GetUtcNow();
        var writes = new ExecutionWrites(scheduling, workOrders, execution, eventLog, events);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var locked = await writes.LockAsync(command.JobId, command.ExpectedVersion, null, withOrder: false, cancellationToken);
        var job = locked.Job.Value;
        var paused = job.Pause();
        if (command.ReasonCodeId is { } reasonId)
        {
            await InventoryRules.ReasonAsync(inventory, reasonId, ReasonKinds.RunEnd, job.SiteId, comment, cancellationToken);
        }

        var run = await execution.GetOpenRunForUpdateAsync(job.Id, cancellationToken)
            ?? throw new DomainRuleException($"Job {job.JobNumber} has no open run.");
        var notes = new List<string>();
        if (command.PartialBatch is { } partial)
        {
            notes.AddRange(await ExecutionWrites.ReportPartialAsync(
                new ProductionReporter(inventory, units, execution, eventLog, events, ids, lineBatches), job, run, partial, comment, now, cancellationToken));
            run = (await execution.GetRunForUpdateAsync(run.Id, cancellationToken))!;
        }

        var ended = run.End(now, command.ReasonCodeId);
        var envelope = events.Create(
            EventTypes.JobPaused, "job", job.Id,
            new { Before = new { job.Status }, After = new { paused.Status }, RunId = run.Id, run.RunNo, ReasonCodeId = command.ReasonCodeId, Comment = comment });
        await execution.UpdateRunAsync(ended, cancellationToken);
        var version = await writes.SaveJobAsync(job, paused, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notes.Insert(0, $"Run {run.RunNo} of {job.JobNumber} ended after {RateCap.Duration(ended.Elapsed(now))}. Start the job again to resume it.");
        return new ExecutionResponse(job.Id, version, run.Id, notes);
    }
}

internal sealed class CompleteJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IExecutionRepository execution,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILineBatchSizes? lineBatches = null) : ICommandHandler<CompleteJobCommand, ExecutionResponse>
{
    public async Task<ExecutionResponse> HandleAsync(CompleteJobCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .RequireComment(command.Comment)
            .Require(command.PartialBatch is null || command.ReasonCodeId is not null, nameof(command.ReasonCodeId), "Say why the job stopped short: a partial batch is an exception.")
            .ThrowIfInvalid();
        var now = timeProvider.GetUtcNow();
        var writes = new ExecutionWrites(scheduling, workOrders, execution, eventLog, events);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var locked = await writes.LockAsync(command.JobId, command.ExpectedVersion, null, withOrder: true, cancellationToken);
        var job = locked.Job.Value;
        var completed = job.Complete(now);
        if (command.ReasonCodeId is { } reasonId)
        {
            await InventoryRules.ReasonAsync(inventory, reasonId, ReasonKinds.RunEnd, job.SiteId, comment, cancellationToken);
        }

        var notes = new List<string>();
        var run = await execution.GetOpenRunForUpdateAsync(job.Id, cancellationToken);
        if (command.PartialBatch is { } partial)
        {
            if (run is null)
            {
                throw new DomainRuleException($"{job.JobNumber} is paused, so its last run has ended; report the partial batch into that run from the job's page.");
            }

            notes.AddRange(await ExecutionWrites.ReportPartialAsync(
                new ProductionReporter(inventory, units, execution, eventLog, events, ids, lineBatches), job, run, partial, comment, now, cancellationToken));
            run = await execution.GetRunForUpdateAsync(run.Id, cancellationToken);
        }

        if (run is not null)
        {
            await execution.UpdateRunAsync(run.End(now, command.ReasonCodeId), cancellationToken);
        }

        var made = await execution.GetMadeAsync(job.Id, cancellationToken);
        var envelope = events.Create(
            EventTypes.JobCompleted, "job", job.Id,
            new { Before = new { job.Status }, After = new { completed.Status }, RunId = run?.Id, Made = made });
        var version = await writes.SaveJobAsync(job, completed, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        if (job.JobType == JobType.Production && made == 0)
        {
            notes.Add($"Nothing was reported for {job.JobNumber}; you can still report into its runs until its work order is closed.");
        }

        if (locked.WorkOrder is { } order && !await workOrders.HasOpenJobsAsync(order.Id, cancellationToken))
        {
            notes.AddIfSet(await writes.RollUpAsync(order, WorkOrderStatus.Completed, "Its last open job completed.", cancellationToken));
        }

        notes.AddRange(await writes.DownstreamNotesAsync(job.Id, now, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new ExecutionResponse(job.Id, version, run?.Id, notes);
    }
}

internal sealed class ReopenJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<ReopenJobCommand, ExecutionResponse>
{
    public async Task<ExecutionResponse> HandleAsync(ReopenJobCommand command, CancellationToken cancellationToken)
    {
        var writes = new ExecutionWrites(scheduling, workOrders, execution, eventLog, events);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var locked = await writes.LockAsync(command.JobId, command.ExpectedVersion, null, withOrder: true, cancellationToken);
        var job = locked.Job.Value;
        if (locked.WorkOrder is { Status: WorkOrderStatus.Closed } closed)
        {
            throw new DomainRuleException($"Work order {closed.OrderNumber} is closed; its jobs can't be reopened.");
        }

        var reopened = job.Reopen();
        var envelope = events.Create(EventTypes.JobReopened, "job", job.Id, new { Before = new { job.Status }, After = new { reopened.Status } });
        var version = await writes.SaveJobAsync(job, reopened, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        var notes = new List<string> { $"{job.JobNumber} is paused; start it to run it again." };
        notes.AddIfSet(await writes.RollUpAsync(locked.WorkOrder, WorkOrderStatus.InProgress, "A job was reopened.", cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new ExecutionResponse(job.Id, version, null, notes);
    }
}

internal sealed class SetExecutionPolicyHandler(
    IUnitOfWork unitOfWork,
    IExecutionRepository execution,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<SetExecutionPolicyCommand, ExecutionPolicyDto>
{
    public async Task<ExecutionPolicyDto> HandleAsync(SetExecutionPolicyCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.RateTolerancePct is >= 100 and <= 1000, nameof(command.RateTolerancePct), "Between 100 and 1000 percent.")
            .Require(command.ConsumptionTolerancePct is >= 100 and <= 1000, nameof(command.ConsumptionTolerancePct), "Between 100 and 1000 percent.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await inventory.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        var current = await execution.GetPolicyForUpdateAsync(command.SiteId, cancellationToken);
        if ((current?.Version ?? 0) != command.ExpectedVersion)
        {
            throw new ConflictException("The site's reporting tolerances were changed by someone else. Reload and try again.");
        }

        var before = current?.Value ?? ExecutionPolicy.Default;
        var after = new ExecutionPolicy(command.RateTolerancePct, command.ConsumptionTolerancePct);
        if (current is not null && before == after)
        {
            return Dto(command.SiteId, after, current.Version);
        }

        var envelope = events.Create(EventTypes.ExecutionPolicyChanged, "equipment", command.SiteId, new { Before = before, After = after });
        var version = await execution.UpsertPolicyAsync(command.SiteId, after, timeProvider.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Dto(command.SiteId, after, version);
    }

    private static ExecutionPolicyDto Dto(Guid siteId, ExecutionPolicy policy, long version) => new()
    {
        SiteId = siteId,
        RateTolerancePct = policy.RateTolerancePct,
        ConsumptionTolerancePct = policy.ConsumptionTolerancePct,
        Version = version,
    };
}
