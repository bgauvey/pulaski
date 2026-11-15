using Pulaski.Application.Errors;
using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Quality;

/// <summary>A characteristic's control chart for a material at a site, optionally one line, over a period (D-099).</summary>
public sealed record GetControlChartQuery(Guid SiteId, Guid CharacteristicId, Guid MaterialId, Guid? EquipmentId, DateTimeOffset From, DateTimeOffset To)
    : IQuery<ControlChartDto>;

internal sealed class GetControlChartHandler(IQualityReadStore store, TimeProvider timeProvider) : IQueryHandler<GetControlChartQuery, ControlChartDto>
{
    /// <summary>The most points a chart shows: the latest.</summary>
    public const int MaxPoints = 500;

    public async Task<ControlChartDto> HandleAsync(GetControlChartQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        new Validator()
            .Require(query.From < query.To, "To", "The period ends after it starts.")
            .Require(query.To - query.From <= ControlLimitRules.MaxWindow, "From", "A chart covers at most 366 days.")
            .ThrowIfInvalid();
        var characteristic = await store.GetCharacteristicsAsync([query.CharacteristicId], cancellationToken) is [var found, ..]
            ? found
            : throw new NotFoundException($"Characteristic {query.CharacteristicId} not found.");
        var (materialCode, equipmentCode, uomCode) = await store.GetChartCodesAsync(query.CharacteristicId, query.MaterialId, query.EquipmentId, cancellationToken);

        var samples = await store.ListChartSamplesAsync(
            query.SiteId, query.CharacteristicId, query.MaterialId, query.EquipmentId, query.From, query.To < now ? query.To : now, MaxPoints + 1, cancellationToken);
        var truncated = samples.Count > MaxPoints;
        if (truncated)
        {
            samples = [.. samples.Skip(samples.Count - MaxPoints)];
        }

        var limits = await store.GetLimitsForMaterialAsync(query.CharacteristicId, query.MaterialId, cancellationToken);
        var bySample = samples.ToDictionary(s => s.Subgroup.SampleId);
        var evaluated = SpcEvaluation.Evaluate(characteristic, samples, limits);
        var used = evaluated.Where(e => e.Limit is not null).Select(e => e.Limit!.Id).ToHashSet();
        var plotted = evaluated.Select(e =>
        {
            var sample = bySample[e.Point.SampleId];
            var limit = e.Limit;
            return new ControlChartPointDto
            {
                SampleId = e.Point.SampleId,
                At = e.Point.At,
                Value = e.Point.Value,
                Dispersion = e.Point.Dispersion,
                Size = e.Point.Size,
                LineCode = sample.LineCode,
                LotLabel = sample.LotLabel,
                JobNumber = sample.JobNumber,
                LimitId = limit?.Id,
                Center = limit?.Limits.Center,
                Ucl = limit is null ? null : e.Ucl,
                Lcl = limit is null ? null : e.Lcl,
                DispersionCenter = limit?.Limits.DispersionCenter,
                DispersionUcl = limit?.Limits.DispersionUcl,
                DispersionLcl = limit?.Limits.DispersionLcl,
                IsBeyondLimits = e.IsBeyond,
                IsDispersionBeyondLimits = e.IsDispersionBeyond,
                Rules = e.Rules,
            };
        }).ToList();

        var capability = await CapabilityAsync(characteristic, samples, plotted, query.MaterialId, cancellationToken);
        var limitDtos = (await store.ListLimitsAsync(query.CharacteristicId, now, cancellationToken)).Where(l => used.Contains(l.Id)).ToList();
        return new ControlChartDto
        {
            CharacteristicId = characteristic.Id,
            CharacteristicCode = characteristic.Code,
            CharacteristicName = characteristic.Name,
            UomCode = uomCode,
            Decimals = characteristic.Decimals,
            ChartType = characteristic.ChartType,
            SubgroupSize = characteristic.SubgroupSize,
            MaterialId = query.MaterialId,
            MaterialCode = materialCode,
            EquipmentId = query.EquipmentId,
            EquipmentCode = equipmentCode,
            From = query.From,
            To = query.To,
            Points = plotted,
            Unusable = samples.Count - plotted.Count,
            IsTruncated = truncated,
            MaxPoints = MaxPoints,
            Limits = limitDtos,
            RunRules = characteristic.RunRules,
            Capability = capability,
        };
    }

    // Against the material's released specification now, over the charted results that the chart can take.
    private async Task<CapabilityDto> CapabilityAsync(
        Characteristic characteristic, IReadOnlyList<ChartSample> samples, IReadOnlyList<ControlChartPointDto> plotted, Guid materialId, CancellationToken cancellationToken)
    {
        var spec = await store.GetReleasedSpecAsync(materialId, cancellationToken);
        var item = spec?.Items.FirstOrDefault(i => i.CharacteristicId == characteristic.Id);
        var unstable = plotted.Any(p => p.Rules.Count > 0 || p.IsDispersionBeyondLimits);
        var usable = plotted.Select(p => p.SampleId).ToHashSet();
        var subgroups = samples.Select(s => s.Subgroup).Where(s => usable.Contains(s.SampleId)).ToList();
        var common = new CapabilityDto
        {
            SpecVersion = spec?.VersionNumber,
            Lsl = item?.Lsl,
            Target = item?.Target,
            Usl = item?.Usl,
            MaxDefects = item?.MaxDefects,
            IsUnstable = unstable,
            Subgroups = subgroups.Count,
        };

        if (ChartTypes.IsAttribute(characteristic.ChartType))
        {
            var rate = ProcessCapability.Counted(subgroups);
            return common with { DefectRate = rate.Rate, Ppm = rate.Ppm, Inspected = rate.Inspected, Defects = rate.Defects, NotEnough = rate.Rate is null ? "No samples to rate." : null };
        }

        if (item is null)
        {
            return common with { NotEnough = spec is null ? "The material has no released specification." : $"The material's specification doesn't name {characteristic.Code}." };
        }

        var result = ProcessCapability.Measured(characteristic.ChartType, subgroups, characteristic.SubgroupSize, (double?)item.Lsl, (double?)item.Usl);
        return common with
        {
            Subgroups = result.Subgroups,
            Mean = result.Mean,
            SigmaWithin = result.SigmaWithin,
            SigmaOverall = result.SigmaOverall,
            Cp = result.Cp,
            Cpk = result.Cpk,
            Pp = result.Pp,
            Ppk = result.Ppk,
            Band = result.Band,
            PerformanceBand = result.PerformanceBand,
            NotEnough = result.NotEnough,
        };
    }
}
