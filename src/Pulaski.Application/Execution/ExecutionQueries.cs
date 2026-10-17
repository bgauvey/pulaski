using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Execution;

namespace Pulaski.Application.Execution;

/// <summary>The floor board for a site (D-035): each line's running job and what is next, with readiness.</summary>
public sealed record GetLineBoardQuery(Guid SiteId) : IQuery<IReadOnlyList<LineBoardRowDto>>;

/// <summary>A job's execution: runs, reports, readiness.</summary>
public sealed record GetJobExecutionQuery(Guid JobId) : IQuery<JobExecutionDto>;

public sealed record GetExecutionPolicyQuery(Guid SiteId) : IQuery<ExecutionPolicyDto>;

internal sealed class GetLineBoardHandler(IExecutionReadStore store, TimeProvider timeProvider)
    : IQueryHandler<GetLineBoardQuery, IReadOnlyList<LineBoardRowDto>>
{
    public Task<IReadOnlyList<LineBoardRowDto>> HandleAsync(GetLineBoardQuery query, CancellationToken cancellationToken) =>
        store.GetLineBoardAsync(query.SiteId, timeProvider.GetUtcNow(), cancellationToken);
}

internal sealed class GetJobExecutionHandler(IExecutionReadStore store, TimeProvider timeProvider) : IQueryHandler<GetJobExecutionQuery, JobExecutionDto>
{
    public async Task<JobExecutionDto> HandleAsync(GetJobExecutionQuery query, CancellationToken cancellationToken) =>
        await store.GetJobAsync(query.JobId, timeProvider.GetUtcNow(), cancellationToken) ?? throw new NotFoundException($"Job {query.JobId} not found.");
}

internal sealed class GetExecutionPolicyHandler(IExecutionReadStore store) : IQueryHandler<GetExecutionPolicyQuery, ExecutionPolicyDto>
{
    public async Task<ExecutionPolicyDto> HandleAsync(GetExecutionPolicyQuery query, CancellationToken cancellationToken) =>
        await store.GetPolicyAsync(query.SiteId, cancellationToken) ?? throw new NotFoundException($"Site {query.SiteId} not found.");
}

/// <summary>The site's partial batches waiting for approval (D-036).</summary>
public sealed record ListPendingPartialBatchesQuery(Guid SiteId) : IQuery<IReadOnlyList<PartialBatchDto>>;

internal sealed class ListPendingPartialBatchesHandler(IExecutionReadStore store) : IQueryHandler<ListPendingPartialBatchesQuery, IReadOnlyList<PartialBatchDto>>
{
    public Task<IReadOnlyList<PartialBatchDto>> HandleAsync(ListPendingPartialBatchesQuery query, CancellationToken cancellationToken) =>
        store.ListPendingPartialBatchesAsync(query.SiteId, cancellationToken);
}
