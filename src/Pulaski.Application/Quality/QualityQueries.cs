using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Quality;

public sealed record ListCharacteristicsQuery : IQuery<IReadOnlyList<CharacteristicDto>>;

public sealed record ListSpecsQuery : IQuery<IReadOnlyList<SpecSummaryDto>>;

public sealed record GetSpecQuery(Guid Id) : IQuery<SpecDto>;

public sealed record GetSpecVersionQuery(Guid SpecId, Guid VersionId) : IQuery<SpecVersionDto>;

/// <summary>The material's released specification, to show or print; 404 when it has none.</summary>
public sealed record GetReleasedSpecQuery(Guid MaterialId) : IQuery<ReleasedSpecDto>;

/// <summary>Results at the sites the caller may see (<see cref="QualityResultFilter.SiteIds"/>), narrowed by the rest.</summary>
public sealed record ListQualityResultsQuery(QualityResultFilter Filter) : IQuery<IReadOnlyList<QualityResultDto>>;

internal sealed class ListCharacteristicsHandler(IQualityReadStore store) : IQueryHandler<ListCharacteristicsQuery, IReadOnlyList<CharacteristicDto>>
{
    public Task<IReadOnlyList<CharacteristicDto>> HandleAsync(ListCharacteristicsQuery query, CancellationToken cancellationToken) =>
        store.ListCharacteristicsAsync(cancellationToken);
}

internal sealed class ListSpecsHandler(IQualityReadStore store) : IQueryHandler<ListSpecsQuery, IReadOnlyList<SpecSummaryDto>>
{
    public Task<IReadOnlyList<SpecSummaryDto>> HandleAsync(ListSpecsQuery query, CancellationToken cancellationToken) => store.ListSpecsAsync(cancellationToken);
}

internal sealed class GetSpecHandler(IQualityReadStore store) : IQueryHandler<GetSpecQuery, SpecDto>
{
    public async Task<SpecDto> HandleAsync(GetSpecQuery query, CancellationToken cancellationToken) =>
        await store.GetSpecAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Specification {query.Id} not found.");
}

/// <summary>A version with what still stops it being released, so the editor can show it.</summary>
internal sealed class GetSpecVersionHandler(IQualityReadStore store) : IQueryHandler<GetSpecVersionQuery, SpecVersionDto>
{
    public async Task<SpecVersionDto> HandleAsync(GetSpecVersionQuery query, CancellationToken cancellationToken)
    {
        var version = await store.GetVersionAsync(query.SpecId, query.VersionId, cancellationToken)
            ?? throw new NotFoundException($"Version {query.VersionId} of specification {query.SpecId} not found.");
        if (version.Status != SpecVersionStatus.Draft)
        {
            return version;
        }

        var definition = SpecRules.ToDomain(version.Definition);
        var characteristics = await store.GetCharacteristicsAsync(SpecRules.CharacteristicIds(definition), cancellationToken);
        var problems = definition.ReleaseProblems().Concat(SpecRules.ReferenceProblems(definition, characteristics, malformedOnly: false));
        return version with { Problems = [.. problems.Select(SpecRules.ToDto)] };
    }
}

internal sealed class GetReleasedSpecHandler(IQualityReadStore store) : IQueryHandler<GetReleasedSpecQuery, ReleasedSpecDto>
{
    public async Task<ReleasedSpecDto> HandleAsync(GetReleasedSpecQuery query, CancellationToken cancellationToken) =>
        await store.GetReleasedSpecAsync(query.MaterialId, cancellationToken)
        ?? throw new NotFoundException("The material has no released specification.");
}

internal sealed class ListQualityResultsHandler(IQualityReadStore store) : IQueryHandler<ListQualityResultsQuery, IReadOnlyList<QualityResultDto>>
{
    public Task<IReadOnlyList<QualityResultDto>> HandleAsync(ListQualityResultsQuery query, CancellationToken cancellationToken) =>
        store.ListResultsAsync(query.Filter with { Limit = Math.Clamp(query.Filter.Limit, 1, 2000) }, cancellationToken);
}
