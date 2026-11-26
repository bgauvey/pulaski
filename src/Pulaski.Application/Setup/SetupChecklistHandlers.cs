using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Setup;
using Pulaski.Domain.Setup;

namespace Pulaski.Application.Setup;

/// <summary>An optional step marked not needed at a site (#139).</summary>
public sealed record SetupSkip(string Step, bool IsSkipped, string ChangedBy, DateTimeOffset ChangedAt);

/// <summary>Reads for the setup checklist (#139).</summary>
public interface ISetupReadStore
{
    /// <summary>The site's code, or null when there is no such site.</summary>
    Task<string?> SiteCodeAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>How many of each step's things there are at the site (or, without one, enterprise-wide where it applies).</summary>
    Task<SetupFacts> FactsAsync(Guid? siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SetupSkip>> SkipsAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>Writes for the setup checklist (#139), in the current unit of work.</summary>
public interface ISetupRepository
{
    /// <summary>Locks the site row, so marks at a site change one at a time; false when it isn't a site.</summary>
    Task<bool> LockSiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task UpsertSkipAsync(Guid siteId, SetupSkip skip, CancellationToken cancellationToken);
}

/// <summary>The checklist at <paramref name="SiteId"/>; without one (no site yet), the first step is to make it.</summary>
public sealed record GetSetupChecklistQuery(Guid? SiteId) : IQuery<SetupChecklistDto>;

/// <summary>Marks an optional step not needed at a site, or needed after all (#139).</summary>
public sealed record MarkSetupStepCommand(Guid SiteId, string Step, bool NotNeeded) : ICommand<SetupChecklistDto>;

internal sealed class GetSetupChecklistHandler(ISetupReadStore store) : IQueryHandler<GetSetupChecklistQuery, SetupChecklistDto>
{
    public async Task<SetupChecklistDto> HandleAsync(GetSetupChecklistQuery query, CancellationToken cancellationToken)
    {
        var code = query.SiteId is { } id ? await store.SiteCodeAsync(id, cancellationToken) ?? throw new NotFoundException($"Site {id} not found.") : null;
        var facts = await store.FactsAsync(query.SiteId, cancellationToken);
        var skips = query.SiteId is { } site ? await store.SkipsAsync(site, cancellationToken) : [];
        var marked = skips.Where(s => s.IsSkipped).ToDictionary(s => s.Step);
        var steps = SetupChecklist.Evaluate(facts, marked.Keys.ToHashSet());
        return new SetupChecklistDto
        {
            SiteId = query.SiteId,
            SiteCode = code,
            Steps = [.. steps.Select(s => new SetupStepDto
            {
                Key = s.Step.Key,
                Title = s.Step.Title,
                Why = s.Step.Why,
                Href = s.Step.Href,
                Permission = s.Step.Permission,
                Required = s.Step.Required,
                Status = s.Status,
                Count = s.Count,
                MarkedBy = s.Status == SetupStatus.NotNeeded ? marked[s.Step.Key].ChangedBy : null,
                MarkedAt = s.Status == SetupStatus.NotNeeded ? marked[s.Step.Key].ChangedAt : null,
            })],
            RequiredDone = steps.Count(s => s.Step.Required && s.Status == SetupStatus.Done),
            RequiredTotal = steps.Count(s => s.Step.Required),
            Ready = SetupChecklist.Ready(steps),
        };
    }
}

internal sealed class MarkSetupStepHandler(
    IUnitOfWork unitOfWork, ISetupRepository setup, IEventLog eventLog, IEventFactory events, IQueryHandler<GetSetupChecklistQuery, SetupChecklistDto> checklist)
    : ICommandHandler<MarkSetupStepCommand, SetupChecklistDto>
{
    public async Task<SetupChecklistDto> HandleAsync(MarkSetupStepCommand command, CancellationToken cancellationToken)
    {
        var step = SetupChecklist.Skippable(command.Step);
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken))
        {
            if (!await setup.LockSiteAsync(command.SiteId, cancellationToken))
            {
                throw new NotFoundException($"Site {command.SiteId} not found.");
            }

            var envelope = events.Create(EventTypes.SetupStepMarked, "site", command.SiteId, new { Step = step.Key, command.NotNeeded });
            await setup.UpsertSkipAsync(command.SiteId, new SetupSkip(step.Key, command.NotNeeded, envelope.ActorId, envelope.OccurredAt), cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await checklist.HandleAsync(new GetSetupChecklistQuery(command.SiteId), cancellationToken);
    }
}
