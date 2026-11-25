using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Instructions;
using Pulaski.Domain;
using Pulaski.Domain.Instructions;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Instructions;

/// <summary>A released instruction's checklist of one phase that applies to a job on a line (#126c).</summary>
public sealed record ApplicableChecklist(Guid InstructionId, string Code, Guid VersionId, Checklist Checklist);

/// <summary>The job a checklist is answered for, locked: its site, where it is planned, whether that is a line, and its state.</summary>
public sealed record ChecklistJob(Guid Id, Guid SiteId, string JobNumber, string Status, Guid EquipmentId, bool OnLine);

/// <summary>A checklist answered, as kept (#126c).</summary>
public sealed record ChecklistResponse(
    Guid Id, Guid SiteId, Guid JobId, Guid EquipmentId, Guid? RunId, Guid InstructionId, Guid VersionId, string Phase, ChecklistResult Result,
    DateTimeOffset CompletedAt, string CompletedBy);

/// <summary>Checklists' writes and the checks of start and report (#126c, D-116), in the current unit of work.</summary>
public interface IChecklistRepository
{
    /// <summary>The job, locked (<c>FOR UPDATE</c>) as reports lock it; null when there isn't one.</summary>
    Task<ChecklistJob?> LockJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The released checklists of <paramref name="phase"/> that apply to the job on <paramref name="lineId"/>, by instruction code.</summary>
    Task<IReadOnlyList<ApplicableChecklist>> ApplicableAsync(Guid jobId, Guid lineId, string phase, CancellationToken cancellationToken);

    /// <summary>The instructions with a passed checklist of <paramref name="phase"/> for the job on <paramref name="lineId"/>.</summary>
    Task<IReadOnlyList<Guid>> PassedAsync(Guid jobId, Guid lineId, string phase, CancellationToken cancellationToken);

    /// <summary>The job's open run on the line, if one is.</summary>
    Task<Guid?> OpenRunAsync(Guid jobId, Guid lineId, CancellationToken cancellationToken);

    Task InsertAsync(ChecklistResponse response, CancellationToken cancellationToken);

    /// <summary>Puts the job's answers on the line not yet in a run into <paramref name="runId"/>, the run just started.</summary>
    Task AttachToRunAsync(Guid jobId, Guid lineId, Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// What a start or a report needs (#126c): every applicable checklist of the phase passed for the job on the line, once per job and line. A
/// failed one doesn't count.
/// </summary>
public static class ChecklistGate
{
    public static async Task EnsureAsync(
        IChecklistRepository? checklists, Guid jobId, string jobNumber, Guid lineId, IReadOnlyList<string> phases, CancellationToken cancellationToken)
    {
        if (checklists is null)
        {
            return;
        }

        foreach (var phase in phases)
        {
            var passed = await checklists.PassedAsync(jobId, lineId, phase, cancellationToken);
            var missing = (await checklists.ApplicableAsync(jobId, lineId, phase, cancellationToken)).Where(c => !passed.Contains(c.InstructionId)).Select(c => c.Code).ToList();
            if (missing.Count > 0)
            {
                throw phase == ChecklistPhases.Setup
                    ? new DomainRuleException($"Complete the setup checklist of {string.Join(", ", missing)} for {jobNumber} on this line first.")
                    : new DomainRuleException($"Complete the first-article checklist of {string.Join(", ", missing)} for {jobNumber} on this line before reporting.");
            }
        }
    }
}

/// <summary>
/// Answers a released instruction's <paramref name="Phase"/> checklist for a job on its line (#126c). Kept whether it passes or fails; with the open
/// run, else the next run started there.
/// </summary>
public sealed record CompleteChecklistCommand(Guid JobId, Guid InstructionId, string Phase, IReadOnlyDictionary<int, string?> Answers)
    : ICommand<ChecklistCompletedResponse>;

internal sealed class CompleteChecklistHandler(
    IUnitOfWork unitOfWork, IChecklistRepository checklists, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CompleteChecklistCommand, ChecklistCompletedResponse>
{
    public async Task<ChecklistCompletedResponse> HandleAsync(CompleteChecklistCommand command, CancellationToken cancellationToken)
    {
        if (!ChecklistPhases.All.Contains(command.Phase))
        {
            throw new DomainRuleException($"'{command.Phase}' isn't a checklist's phase; use setup or first_article.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var job = await checklists.LockJobAsync(command.JobId, cancellationToken) ?? throw new NotFoundException($"Job {command.JobId} not found.");
        if (!JobStatus.Open.Contains(job.Status))
        {
            throw new DomainRuleException($"{job.JobNumber} is {UserMessage.Code(job.Status)}; its checklists are closed.");
        }

        if (!job.OnLine)
        {
            throw new DomainRuleException($"{job.JobNumber} is waiting on its department: answer its checklists once it runs on a line.");
        }

        var checklist = (await checklists.ApplicableAsync(job.Id, job.EquipmentId, command.Phase, cancellationToken)).FirstOrDefault(c => c.InstructionId == command.InstructionId)
            ?? throw new DomainRuleException($"That instruction has no released {UserMessage.Code(command.Phase)} checklist for {job.JobNumber}.");
        var result = Checklists.Evaluate(checklist.Checklist, command.Answers);
        var envelope = events.Create(EventTypes.ChecklistCompleted, "job", job.Id, new
        {
            checklist.Code,
            checklist.InstructionId,
            checklist.VersionId,
            command.Phase,
            LineId = job.EquipmentId,
            result.Passed,
            result.Answers,
        });
        var response = new ChecklistResponse(
            ids.NewId(), job.SiteId, job.Id, job.EquipmentId, await checklists.OpenRunAsync(job.Id, job.EquipmentId, cancellationToken),
            checklist.InstructionId, checklist.VersionId, command.Phase, result, envelope.OccurredAt, envelope.ActorId);
        await checklists.InsertAsync(response, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ChecklistCompletedResponse(response.Id, result.Passed, [.. result.Answers.Where(a => !a.Ok).Select(a => a.Item)]);
    }
}

/// <summary>Checklists between the contract and the domain (#126c).</summary>
public static class ChecklistMapping
{
    public static Checklist ToDomain(ChecklistDto dto) =>
        new(dto.Phase, [.. dto.Items.Select(i => new ChecklistItem(i.Prompt, i.Kind, i.Required, i.Expected, i.Min, i.Max, i.Unit, i.Choices))]);

    public static ChecklistDto ToDto(Checklist checklist) => new()
    {
        Phase = checklist.Phase,
        Items = [.. checklist.Items.Select(i => new ChecklistItemDto
        {
            Prompt = i.Prompt,
            Kind = i.Kind,
            Required = i.Required,
            Expected = i.Expected,
            Min = i.Min,
            Max = i.Max,
            Unit = i.Unit,
            Choices = i.Choices,
        })],
    };
}
