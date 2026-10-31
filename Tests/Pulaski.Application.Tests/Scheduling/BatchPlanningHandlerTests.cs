using Pulaski.Application.Materials;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

/// <summary>Jobs of a material made in batches are whole batches (D-036).</summary>
public class BatchPlanningHandlerTests : SchedulingHandlerTestBase
{
    public BatchPlanningHandlerTests() => F.Stores.Boms.AddMaterial(White, Metre, batchSize: 50m);

    [Fact]
    public async Task A_job_is_whole_batches()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, wo, "J-1", qty: 125m));
        Assert.Contains("Plan 100 or 150", ex.Message, StringComparison.Ordinal);
        await CreateProductionAsync(line, wo, "J-2", qty: 150m);
    }

    [Fact]
    public async Task A_replacement_rounds_what_is_left_up_to_whole_batches()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White, WorkOrderStatus.InProgress);
        var job = F.Stores.Scheduling.AddJob(global::Pulaski.Domain.Scheduling.Job.CreateProduction(
            Guid.CreateVersion7(), Site, wo.Id, "J-9", line.Id, White, 200m, Metre, 1m, null) with
        { Status = JobStatus.Running });
        F.Stores.Scheduling.SetGoodQty(job.Id, 125m);

        var cancel = new CancelJobHandler(
            F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time);
        var result = await cancel.HandleAsync(new CancelJobCommand(job.Id, Version(job.Id), "Line down", Replace: true), CancellationToken.None);

        Assert.Equal(100m, F.Stores.Scheduling.FindJob(result.ReplacementJobId!.Value)!.Value.PlannedQty);
    }
}

/// <summary>Replacing a job with exactly what is left, a partial batch included (D-067).</summary>
public class PartialReplacementHandlerTests : SchedulingHandlerTestBase
{
    public PartialReplacementHandlerTests() => F.Stores.Boms.AddMaterial(White, Metre, batchSize: 50m);

    private CancelJobHandler Cancel() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time,
        F.Stores.LineBatchSizes);

    private async Task<global::Pulaski.Domain.Scheduling.Job> StartedAsync(SchedulingLine line, decimal planned, decimal made)
    {
        var wo = await WorkOrderAsync(White, WorkOrderStatus.InProgress);
        var job = F.Stores.Scheduling.AddJob(global::Pulaski.Domain.Scheduling.Job.CreateProduction(
            Guid.CreateVersion7(), Site, wo.Id, "J-" + Guid.NewGuid().ToString("N")[..4], line.Id, White, planned, Metre, 1m, null) with
        { Status = JobStatus.Running });
        F.Stores.Scheduling.SetGoodQty(job.Id, made);
        return job;
    }

    [Fact]
    public async Task Exactly_what_is_left_is_planned_with_a_partial_last_batch_and_may_move_to_any_line()
    {
        var line = Line();
        var big = Line();
        F.Stores.LineBatchSizes.Sizes[(big.Id, White)] = 200m;
        var job = await StartedAsync(line, 200m, 125m);

        var result = await Cancel().HandleAsync(new CancelJobCommand(job.Id, Version(job.Id), "Line down", Replace: true, ReplaceExact: true), CancellationToken.None);

        var replacement = F.Stores.Scheduling.FindJob(result.ReplacementJobId!.Value)!.Value;
        Assert.Equal((75m, true), (replacement.PlannedQty!.Value, replacement.PartialBatchPlanned));
        Assert.StartsWith("1 batch of 50", result.Note, StringComparison.Ordinal);
        Assert.Contains("and a partial batch of 25", result.Note, StringComparison.Ordinal);

        // A whole remainder isn't a partial; and the planned partial moves to a line of another size.
        var whole = await StartedAsync(line, 200m, 100m);
        var wholeResult = await Cancel().HandleAsync(new CancelJobCommand(whole.Id, Version(whole.Id), null, Replace: true, ReplaceExact: true), CancellationToken.None);
        Assert.False(F.Stores.Scheduling.FindJob(wholeResult.ReplacementJobId!.Value)!.Value.PartialBatchPlanned);
        var sequence = new SequenceJobHandler(F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.Events, F.Events, F.Time, F.Stores.Boms, F.Stores.Units, F.Stores.LineBatchSizes);
        await sequence.HandleAsync(new SequenceJobCommand(replacement.Id, Version(replacement.Id), big.Id, null), CancellationToken.None);
        Assert.Equal(big.Id, F.Stores.Scheduling.FindJob(replacement.Id)!.Value.EquipmentId);
    }
}

/// <summary>A line's own batch size (D-065) decides whole batches on that line.</summary>
public class LineBatchSizeHandlerTests : SchedulingHandlerTestBase
{
    public LineBatchSizeHandlerTests() => F.Stores.Boms.AddMaterial(White, Metre, batchSize: 50m);

    private CreateJobHandler CreateOnLines() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time,
        F.Stores.LineBatchSizes);

    [Fact]
    public async Task A_job_is_whole_batches_of_its_lines_size_and_moves_only_to_a_line_where_it_still_is()
    {
        var big = Line();
        var small = Line();
        F.Stores.LineBatchSizes.Sizes[(big.Id, White)] = 200m;
        var wo = await WorkOrderAsync(White);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateOnLines().HandleAsync(
            new CreateJobCommand(big.Id, "J-1", JobType.Production, wo.Id, 150m, null, false, null, null), CancellationToken.None));
        Assert.Contains("batches of 200", ex.Message, StringComparison.Ordinal);
        var onSmall = (await CreateOnLines().HandleAsync(
            new CreateJobCommand(small.Id, "J-2", JobType.Production, wo.Id, 150m, null, false, null, null), CancellationToken.None)).Id;

        var sequence = new SequenceJobHandler(F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.Events, F.Events, F.Time, F.Stores.Boms, F.Stores.Units, F.Stores.LineBatchSizes);
        var moved = await Assert.ThrowsAsync<DomainRuleException>(() => sequence.HandleAsync(new SequenceJobCommand(onSmall, Version(onSmall), big.Id, null), CancellationToken.None));
        Assert.Contains("on " + big.Code, moved.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replacement_rounds_up_to_whole_batches_of_its_lines_size()
    {
        var line = Line();
        F.Stores.LineBatchSizes.Sizes[(line.Id, White)] = 200m;
        var wo = await WorkOrderAsync(White, WorkOrderStatus.InProgress);
        var job = F.Stores.Scheduling.AddJob(global::Pulaski.Domain.Scheduling.Job.CreateProduction(
            Guid.CreateVersion7(), Site, wo.Id, "J-9", line.Id, White, 400m, Metre, 1m, null) with
        { Status = JobStatus.Running });
        F.Stores.Scheduling.SetGoodQty(job.Id, 150m);

        var cancel = new CancelJobHandler(
            F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time,
            F.Stores.LineBatchSizes);
        var result = await cancel.HandleAsync(new CancelJobCommand(job.Id, Version(job.Id), "Line down", Replace: true), CancellationToken.None);

        // 250 left is 1.25 batches of 200 on this line: two batches, not five of the material's 50.
        Assert.Equal(400m, F.Stores.Scheduling.FindJob(result.ReplacementJobId!.Value)!.Value.PlannedQty);
    }
}

/// <summary>Replacing a job with only the whole batches left, leaving the order short (#85).</summary>
public class RoundDownReplacementHandlerTests : SchedulingHandlerTestBase
{
    public RoundDownReplacementHandlerTests() => F.Stores.Boms.AddMaterial(White, Metre, batchSize: 50m);

    private CancelJobHandler Cancel() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time,
        F.Stores.LineBatchSizes);

    private async Task<global::Pulaski.Domain.Scheduling.Job> StartedAsync(SchedulingLine line, decimal planned, decimal made)
    {
        var wo = await WorkOrderAsync(White, WorkOrderStatus.InProgress);
        var job = F.Stores.Scheduling.AddJob(global::Pulaski.Domain.Scheduling.Job.CreateProduction(
            Guid.CreateVersion7(), Site, wo.Id, "J-" + Guid.NewGuid().ToString("N")[..4], line.Id, White, planned, Metre, 1m, null) with
        { Status = JobStatus.Running });
        F.Stores.Scheduling.SetGoodQty(job.Id, made);
        return job;
    }

    [Fact]
    public async Task What_is_left_is_rounded_down_to_whole_batches_of_the_lines_size_and_says_the_order_is_short()
    {
        var line = Line();
        var job = await StartedAsync(line, planned: 200m, made: 75m);

        var result = await Cancel().HandleAsync(new CancelJobCommand(job.Id, Version(job.Id), "Line down", Replace: true, RoundDown: true), CancellationToken.None);

        // 125 left: two batches of 50, 25 short.
        var replacement = F.Stores.Scheduling.FindJob(result.ReplacementJobId!.Value)!.Value;
        Assert.Equal((100m, false), (replacement.PlannedQty, replacement.PartialBatchPlanned));
        Assert.Contains("short 25", result.Note, StringComparison.Ordinal);

        // On a line of 200 kg batches, 250 left is one batch of 200.
        var big = Line();
        F.Stores.LineBatchSizes.Sizes[(big.Id, White)] = 200m;
        var bigJob = await StartedAsync(big, planned: 400m, made: 150m);
        var bigResult = await Cancel().HandleAsync(new CancelJobCommand(bigJob.Id, Version(bigJob.Id), null, Replace: true, RoundDown: true), CancellationToken.None);
        Assert.Equal(200m, F.Stores.Scheduling.FindJob(bigResult.ReplacementJobId!.Value)!.Value.PlannedQty);
    }

    [Fact]
    public async Task Less_than_a_batch_left_or_asking_for_exact_too_is_refused()
    {
        var line = Line();
        var job = await StartedAsync(line, planned: 100m, made: 75m);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Cancel().HandleAsync(new CancelJobCommand(job.Id, Version(job.Id), null, Replace: true, RoundDown: true), CancellationToken.None));
        Assert.Contains("Less than a batch", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<Errors.ValidationException>(() =>
            Cancel().HandleAsync(new CancelJobCommand(job.Id, Version(job.Id), null, Replace: true, ReplaceExact: true, RoundDown: true), CancellationToken.None));
    }
}

/// <summary>Setting a material's batching (D-036).</summary>
public class SetBatchingHandlerTests
{
    private readonly HandlerFixture _f = new();

    [Fact]
    public async Task The_rule_defaults_from_the_type_and_open_orders_that_no_longer_fit_are_named()
    {
        var kg = _f.Stores.Units.AddUnit("kg", "mass", 1m).Id;
        var admin = _f.Stores.MaterialAdmin;
        admin.AddUom("kg", id: kg);
        var material = Material.CreateLocal(Guid.CreateVersion7(), new MaterialChange("FG-1", "Finished", null, "finished", null, kg, true, null, true));
        var version = await admin.InsertAsync(material, HandlerFixture.Now, CancellationToken.None);
        admin.OpenOrders[material.Id] = [("WO-1", 100m, kg), ("WO-2", 125m, kg)];

        var handler = new SetBatchingHandler(_f.Stores.UnitOfWork, admin, _f.Stores.Units, _f.Stores.Events, _f.Events);
        var result = await handler.HandleAsync(new SetBatchingCommand(material.Id, version, 50m, 2m, null), CancellationToken.None);

        Assert.Equal((50m, BatchRule.Exact), (admin.Batching[material.Id].Size, admin.Batching[material.Id].Rule));
        Assert.Contains("WO-2", Assert.Single(result.StillUsedBy), StringComparison.Ordinal);
        Assert.DoesNotContain("WO-1", result.StillUsedBy[0], StringComparison.Ordinal);

        await handler.HandleAsync(new SetBatchingCommand(material.Id, result.Version, null, 2m, null), CancellationToken.None);
        Assert.Null(admin.Batching[material.Id].Size);
    }
}
