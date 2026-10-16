using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Processes;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Processes;

/// <summary>Processes, optionally only those whose scope covers <see cref="MaterialId"/> (what order creation will offer).</summary>
public sealed record ListProcessesQuery(Guid? SiteId, Guid? MaterialId = null) : IQuery<IReadOnlyList<ProcessSummaryDto>>;

public sealed record GetProcessQuery(Guid Id) : IQuery<ProcessDto>;

public sealed record GetProcessVersionQuery(Guid ProcessId, Guid VersionId) : IQuery<ProcessVersionDto>;

internal sealed class ListProcessesHandler(IProcessReadStore store) : IQueryHandler<ListProcessesQuery, IReadOnlyList<ProcessSummaryDto>>
{
    public Task<IReadOnlyList<ProcessSummaryDto>> HandleAsync(ListProcessesQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, query.MaterialId, cancellationToken);
}

internal sealed class GetProcessHandler(IProcessReadStore store) : IQueryHandler<GetProcessQuery, ProcessDto>
{
    public async Task<ProcessDto> HandleAsync(GetProcessQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Process {query.Id} not found.");
}

/// <summary>A version with what still stops it being released, so the editor can show it.</summary>
internal sealed class GetProcessVersionHandler(IProcessReadStore store) : IQueryHandler<GetProcessVersionQuery, ProcessVersionDto>
{
    public async Task<ProcessVersionDto> HandleAsync(GetProcessVersionQuery query, CancellationToken cancellationToken)
    {
        var version = await store.GetVersionAsync(query.ProcessId, query.VersionId, cancellationToken)
            ?? throw new NotFoundException($"Version {query.VersionId} of process {query.ProcessId} not found.");
        if (version.Status != ProcessVersionStatus.Draft)
        {
            return version;
        }

        var process = await store.GetAsync(query.ProcessId, cancellationToken)
            ?? throw new NotFoundException($"Process {query.ProcessId} not found.");
        var definition = ProcessRules.ToDomain(version.Definition);
        var equipment = await store.GetEquipmentAsync(ProcessRules.EquipmentIds(definition), cancellationToken);
        return version with
        {
            Problems = [.. definition.ReleaseProblems()
                .Concat(ProcessRules.EquipmentProblems(definition, process.SiteId, equipment, malformedOnly: false))
                .Select(ProcessRules.ToDto)],
        };
    }
}
