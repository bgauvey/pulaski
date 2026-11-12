using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Quality;

/// <summary>Write side for quality (#65, D-085); runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IQualityRepository
{
    Task<Versioned<Characteristic>?> GetCharacteristicForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The characteristics, without locks (code and kind never change).</summary>
    Task<IReadOnlyList<Characteristic>> GetCharacteristicsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<bool> CharacteristicCodeExistsAsync(string code, CancellationToken cancellationToken);

    Task<long> InsertCharacteristicAsync(Characteristic characteristic, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateCharacteristicAsync(Characteristic characteristic, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The material's code and whether it is active; null when there is no such material.</summary>
    Task<(string Code, bool IsActive)?> GetMaterialAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> SpecExistsForMaterialAsync(Guid materialId, CancellationToken cancellationToken);

    Task<Versioned<MaterialSpec>?> GetSpecForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertSpecAsync(MaterialSpec spec, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<SpecVersion>?> GetVersionForUpdateAsync(Guid specId, Guid versionId, CancellationToken cancellationToken);

    Task<Versioned<SpecVersion>?> GetReleasedForUpdateAsync(Guid specId, CancellationToken cancellationToken);

    Task<bool> HasDraftAsync(Guid specId, CancellationToken cancellationToken);

    Task<long> InsertVersionAsync(SpecVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateVersionAsync(SpecVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes a released version's items as fixed rows, which results point to (D-010).</summary>
    Task InsertReleasedItemsAsync(SpecVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// The item of the material's released specification for the characteristic, read <c>FOR SHARE</c> so a release can't slip in between;
    /// null when the material has no active specification with a released version, or it doesn't name the characteristic.
    /// </summary>
    Task<(SpecItem Item, int VersionNumber)?> GetItemInForceAsync(Guid materialId, Guid characteristicId, CancellationToken cancellationToken);

    /// <summary>The job a result is recorded against, with the run it had at <paramref name="at"/> (the one open then, else none).</summary>
    Task<QualityJobContext?> GetJobContextAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Where a lot was made: its production declaration's job, run and effective time; null for a lot not made in a run.</summary>
    Task<LotProduction?> GetLotProductionAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>A reason's id by kind and code, the site's own first, else enterprise-wide; null when there is none active.</summary>
    Task<Guid?> FindReasonAsync(string kind, string code, Guid siteId, CancellationToken cancellationToken);

    Task InsertResultAsync(QualityResultRow result, CancellationToken cancellationToken);

    /// <summary>A result, locked; null when there is none with that id at that time.</summary>
    Task<QualityResultRow?> GetResultForUpdateAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    Task ExcludeResultAsync(Guid id, DateTimeOffset occurredAt, Guid reasonId, string? note, string by, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The line's code when <paramref name="id"/> is equipment that runs jobs; null otherwise.</summary>
    Task<string?> GetLineCodeAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every period of limits for the characteristic, material and line (null: every line). Lock the characteristic first.</summary>
    Task<IReadOnlyList<ControlLimit>> GetLimitsAsync(Guid characteristicId, Guid materialId, Guid? equipmentId, CancellationToken cancellationToken);

    Task<ControlLimit?> GetLimitAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether the characteristic has limits in force at <paramref name="at"/> or starting later.</summary>
    Task<bool> HasOpenLimitsAsync(Guid characteristicId, DateTimeOffset at, CancellationToken cancellationToken);

    Task InsertLimitAsync(ControlLimit limit, CancellationToken cancellationToken);

    /// <summary>Writes a period's end.</summary>
    Task EndLimitAsync(Guid id, DateTimeOffset endsAt, string? by, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A baseline's results, read in the command's transaction: see <see cref="IQualityReadStore.ListBaselineAsync"/>.</summary>
    Task<IReadOnlyList<SpcSubgroup>> ListBaselineAsync(Guid characteristicId, Guid materialId, Guid? equipmentId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}

/// <summary>Read side for quality (#65): DTOs straight from SQL, no locks.</summary>
public interface IQualityReadStore
{
    Task<IReadOnlyList<CharacteristicDto>> ListCharacteristicsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Characteristic>> GetCharacteristicsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpecSummaryDto>> ListSpecsAsync(CancellationToken cancellationToken);

    Task<SpecDto?> GetSpecAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One version with its definition; <see cref="SpecVersionDto.Problems"/> is not filled.</summary>
    Task<SpecVersionDto?> GetVersionAsync(Guid specId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>The material's released specification with its characteristics, in their order; null when it has none.</summary>
    Task<ReleasedSpecDto?> GetReleasedSpecAsync(Guid materialId, CancellationToken cancellationToken);

    Task<QualityResultDto?> GetResultAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    /// <summary>Results, newest first, at most <see cref="QualityResultFilter.Limit"/>.</summary>
    Task<IReadOnlyList<QualityResultDto>> ListResultsAsync(QualityResultFilter filter, CancellationToken cancellationToken);

    /// <summary>Every period of the characteristic's control limits, newest first within each material and line.</summary>
    Task<IReadOnlyList<ControlLimitDto>> ListLimitsAsync(Guid characteristicId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The results that could make a baseline: the characteristic's results for the material sampled in <c>[windowStart, windowEnd)</c>, not excluded, on the
    /// line when one is given (results against a job carry its run's line), in time order, at most <see cref="MaxBaseline"/>.
    /// </summary>
    Task<IReadOnlyList<SpcSubgroup>> ListBaselineAsync(Guid characteristicId, Guid materialId, Guid? equipmentId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>
    /// A chart's results: the characteristic's at the site for the material sampled in <c>[windowStart, windowEnd)</c>, not excluded, on the line when
    /// given; the latest <paramref name="limit"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<ChartSample>> ListChartSamplesAsync(
        Guid siteId, Guid characteristicId, Guid materialId, Guid? equipmentId, DateTimeOffset windowStart, DateTimeOffset windowEnd, int limit, CancellationToken cancellationToken);

    /// <summary>Every period of limits of the characteristic for the material, any line.</summary>
    Task<IReadOnlyList<ControlLimit>> GetLimitsForMaterialAsync(Guid characteristicId, Guid materialId, CancellationToken cancellationToken);

    /// <summary>The material's code, the line's when one is given, and the characteristic's unit.</summary>
    Task<(string MaterialCode, string? EquipmentCode, string? UomCode)> GetChartCodesAsync(
        Guid characteristicId, Guid materialId, Guid? equipmentId, CancellationToken cancellationToken);

    /// <summary>The most results a baseline reads.</summary>
    const int MaxBaseline = 1000;
}

/// <summary>Which results to list: at the sites (null = all), narrowed by what is given.</summary>
public sealed record QualityResultFilter(
    IReadOnlyList<Guid>? SiteIds,
    Guid? SiteId = null,
    Guid? MaterialId = null,
    Guid? LotId = null,
    Guid? JobId = null,
    Guid? CharacteristicId = null,
    bool FailingOnly = false,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 500);

/// <summary>A result as a chart takes it: its subgroup, and where it was made.</summary>
public sealed record ChartSample(SpcSubgroup Subgroup, Guid? EquipmentId, string? LineCode, string? LotLabel, string? JobNumber);

public sealed record QualityJobContext(
    Guid JobId, string JobNumber, Guid SiteId, Guid? MaterialId, Guid EquipmentId, Guid? RunId, Guid? RunEquipmentId);

public sealed record LotProduction(Guid JobId, Guid? RunId, DateTimeOffset EffectiveAt);

/// <summary>One row of <c>ts.quality_sample</c>.</summary>
public sealed record QualityResultRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid SiteId,
    Guid CharacteristicId,
    Guid? EquipmentId,
    Guid? JobId,
    Guid? RunId,
    Guid? LotId,
    Guid MaterialId,
    QualityMeasurement Measurement,
    SampleStatistics Statistics,
    Guid? SpecItemId,
    bool? Conforms,
    string Source,
    Guid? EnteredByUserId,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string? Comment)
{
    public bool IsExcluded { get; init; }
}
