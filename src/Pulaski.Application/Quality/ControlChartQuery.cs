using Pulaski.Application.Errors;
using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Quality;
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

        // Only limits worked out for the chart it has now count.
        var limits = (await store.GetLimitsForMaterialAsync(query.CharacteristicId, query.MaterialId, cancellationToken))
            .Where(l => l.ChartType == characteristic.ChartType && l.SubgroupSize == characteristic.SubgroupSize)
            .ToList();
        var bySample = samples.ToDictionary(s => s.Subgroup.SampleId);
        var points = ControlLimits.Points(characteristic.ChartType, samples.Select(s => s.Subgroup));
        var used = new HashSet<Guid>();
        var plotted = new List<ControlChartPointDto>();
        foreach (var p in points)
        {
            var sample = bySample[p.SampleId];
            var limit = ControlLimitPeriods.InForce(limits, sample.EquipmentId, p.At);
            var (ucl, lcl) = limit is null ? (double.NaN, double.NaN) : ControlLimits.At(characteristic.ChartType, limit.Limits, p.Size);
            if (limit is not null)
            {
                used.Add(limit.Id);
            }

            plotted.Add(new ControlChartPointDto
            {
                SampleId = p.SampleId,
                At = p.At,
                Value = p.Value,
                Dispersion = p.Dispersion,
                Size = p.Size,
                LineCode = sample.LineCode,
                LotLabel = sample.LotLabel,
                JobNumber = sample.JobNumber,
                LimitId = limit?.Id,
                Center = limit?.Limits.Center,
                Ucl = limit is null ? null : ucl,
                Lcl = limit is null ? null : lcl,
                DispersionCenter = limit?.Limits.DispersionCenter,
                DispersionUcl = limit?.Limits.DispersionUcl,
                DispersionLcl = limit?.Limits.DispersionLcl,
                IsBeyondLimits = limit is not null && (p.Value > ucl || p.Value < lcl),
                IsDispersionBeyondLimits = limit?.Limits.DispersionUcl is { } du && p.Dispersion is { } d && (d > du || d < (limit.Limits.DispersionLcl ?? 0)),
            });
        }

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
            Unusable = samples.Count - points.Count,
            IsTruncated = truncated,
            MaxPoints = MaxPoints,
            Limits = limitDtos,
        };
    }
}
