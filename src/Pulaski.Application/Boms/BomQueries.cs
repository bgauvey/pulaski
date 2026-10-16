using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Contracts.Boms;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Boms;

public sealed record ListBomsQuery(Guid? MaterialId = null) : IQuery<IReadOnlyList<BomSummaryDto>>;

public sealed record GetBomQuery(Guid Id) : IQuery<BomDto>;

public sealed record GetBomVersionQuery(Guid BomId, Guid VersionId) : IQuery<BomVersionDto>;

/// <summary>The components planned for a work order's jobs (D-025).</summary>
public sealed record ListPlannedComponentsQuery(Guid WorkOrderId) : IQuery<IReadOnlyList<PlannedComponentDto>>;

internal sealed class ListBomsHandler(IBomReadStore store) : IQueryHandler<ListBomsQuery, IReadOnlyList<BomSummaryDto>>
{
    public Task<IReadOnlyList<BomSummaryDto>> HandleAsync(ListBomsQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.MaterialId, cancellationToken);
}

internal sealed class GetBomHandler(IBomReadStore store) : IQueryHandler<GetBomQuery, BomDto>
{
    public async Task<BomDto> HandleAsync(GetBomQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"BOM {query.Id} not found.");
}

internal sealed class ListPlannedComponentsHandler(IBomReadStore store) : IQueryHandler<ListPlannedComponentsQuery, IReadOnlyList<PlannedComponentDto>>
{
    public Task<IReadOnlyList<PlannedComponentDto>> HandleAsync(ListPlannedComponentsQuery query, CancellationToken cancellationToken) =>
        store.ListPlannedComponentsAsync(query.WorkOrderId, cancellationToken);
}

/// <summary>A version with what still stops it being released, so the editor can show it.</summary>
internal sealed class GetBomVersionHandler(IBomReadStore store, IProcessReadStore processes) : IQueryHandler<GetBomVersionQuery, BomVersionDto>
{
    public async Task<BomVersionDto> HandleAsync(GetBomVersionQuery query, CancellationToken cancellationToken)
    {
        var version = await store.GetVersionAsync(query.BomId, query.VersionId, cancellationToken)
            ?? throw new NotFoundException($"Version {query.VersionId} of BOM {query.BomId} not found.");
        if (version.Status != BomVersionStatus.Draft)
        {
            return version;
        }

        var header = (await store.GetAsync(query.BomId, cancellationToken))!;
        var bom = new Bom(header.Id, header.MaterialId, header.ProcessId, header.IsActive);
        var definition = BomRules.ToDomain(version.Definition);
        var materials = await store.GetMaterialsAsync(BomRules.MaterialIds(definition), cancellationToken);
        var problems = definition.ReleaseProblems()
            .Concat(BomRules.ReferenceProblems(definition, bom, materials, await OperationsAsync(bom, cancellationToken), malformedOnly: false));
        return version with { Problems = [.. problems.Select(BomRules.ToDto)] };
    }

    private async Task<IReadOnlySet<int>?> OperationsAsync(Bom bom, CancellationToken cancellationToken)
    {
        if (bom.ProcessId is not { } processId
            || await processes.GetAsync(processId, cancellationToken) is not { } process
            || process.Versions.FirstOrDefault(v => v.Status == ProcessVersionStatus.Released) is not { } released)
        {
            return null;
        }

        var route = await processes.GetVersionAsync(processId, released.Id, cancellationToken);
        return route?.Definition.Operations.Select(o => o.Number).ToHashSet();
    }
}
