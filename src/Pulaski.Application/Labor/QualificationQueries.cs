using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Labor;

namespace Pulaski.Application.Labor;

/// <summary>A grant with the names the screens show (D-048).</summary>
public sealed record GrantRow(GrantRecord Grant, string PersonName, string EmployeeNumber, Guid SiteId, string QualificationCode, string QualificationName, long Version);

/// <summary>A requirement on equipment, directly or (with another <c>FromEquipmentId</c>) through an ancestor.</summary>
public sealed record RequirementRow(Guid QualificationId, string Code, string Name, Guid FromEquipmentId, string FromEquipmentCode, bool IsOwn);

/// <summary>Read side for qualifications (D-048).</summary>
public interface IQualificationReadStore
{
    /// <summary>The site's qualifications, switched-off ones too, with the codes of the equipment that requires each.</summary>
    Task<IReadOnlyList<QualificationDto>> ListQualificationsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>Every grant at the site, revoked and expired ones too.</summary>
    Task<IReadOnlyList<GrantRow>> ListGrantsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>What the equipment requires itself and through the equipment above it; null when it doesn't exist.</summary>
    Task<IReadOnlyList<RequirementRow>?> ListRequirementsAsync(Guid equipmentId, CancellationToken cancellationToken);
}

public sealed record ListQualificationsQuery(Guid SiteId) : IQuery<IReadOnlyList<QualificationDto>>;

/// <summary>
/// A site's grants (D-048), of one person or qualification when given; with <c>ExpiringWithinDays</c> only valid grants that expire
/// within that many days of today.
/// </summary>
public sealed record ListQualificationGrantsQuery(Guid SiteId, Guid? PersonId, Guid? QualificationId, int? ExpiringWithinDays) : IQuery<IReadOnlyList<QualificationGrantDto>>;

public sealed record GetEquipmentQualificationsQuery(Guid EquipmentId) : IQuery<EquipmentQualificationsDto>;

internal static class QualificationView
{
    public const int SoonDays = 30;

    public static async Task<(DateOnly Today, DateTimeOffset Now)> TodayAsync(ILaborReadStore labor, Guid siteId, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var zone = LaborView.Zone(await labor.GetSiteTimeZoneAsync(siteId, cancellationToken));
        return (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime), now);
    }

    public static QualificationGrant Domain(GrantRecord g) => new(g.QualificationId, g.ValidFrom, g.ExpiresOn, g.RevokedAt);

    public static QualificationGrantDto ToDto(GrantRow row, DateOnly today, DateTimeOffset now) => new()
    {
        Id = row.Grant.Id,
        PersonId = row.Grant.PersonId,
        PersonName = row.PersonName,
        EmployeeNumber = row.EmployeeNumber,
        QualificationId = row.Grant.QualificationId,
        QualificationCode = row.QualificationCode,
        QualificationName = row.QualificationName,
        ValidFrom = row.Grant.ValidFrom,
        ExpiresOn = row.Grant.ExpiresOn,
        Status = QualificationRules.Status(Domain(row.Grant), today, now),
        Note = row.Grant.Note,
        GrantedBy = row.Grant.GrantedBy,
        RevokedAt = row.Grant.RevokedAt,
        RevokedBy = row.Grant.RevokedBy,
        RevokeReason = row.Grant.RevokeReason,
        Version = row.Version,
    };
}

internal sealed class ListQualificationsHandler(IQualificationReadStore store, ILaborReadStore labor, TimeProvider timeProvider)
    : IQueryHandler<ListQualificationsQuery, IReadOnlyList<QualificationDto>>
{
    public async Task<IReadOnlyList<QualificationDto>> HandleAsync(ListQualificationsQuery query, CancellationToken cancellationToken)
    {
        var (today, now) = await QualificationView.TodayAsync(labor, query.SiteId, timeProvider, cancellationToken);
        var valid = (await store.ListGrantsAsync(query.SiteId, cancellationToken))
            .Where(g => QualificationRules.Status(QualificationView.Domain(g.Grant), today, now) == GrantStatus.Valid)
            .ToLookup(g => g.Grant.QualificationId);

        return [.. (await store.ListQualificationsAsync(query.SiteId, cancellationToken)).Select(q =>
        {
            // A person counts once, by the grant that lasts longest.
            var holders = valid[q.Id].GroupBy(g => g.Grant.PersonId).Select(p => p.Max(g => g.Grant.ExpiresOn ?? DateOnly.MaxValue)).ToList();
            return q with
            {
                Holders = holders.Count,
                ExpiringSoon = holders.Count(e => e != DateOnly.MaxValue && e <= today.AddDays(QualificationView.SoonDays)),
            };
        })];
    }
}

internal sealed class ListQualificationGrantsHandler(IQualificationReadStore store, ILaborReadStore labor, TimeProvider timeProvider)
    : IQueryHandler<ListQualificationGrantsQuery, IReadOnlyList<QualificationGrantDto>>
{
    public async Task<IReadOnlyList<QualificationGrantDto>> HandleAsync(ListQualificationGrantsQuery query, CancellationToken cancellationToken)
    {
        new Validator().Require(query.ExpiringWithinDays is null or (>= 0 and <= 366), "ExpiringWithinDays", "0 to 366 days.").ThrowIfInvalid();
        var (today, now) = await QualificationView.TodayAsync(labor, query.SiteId, timeProvider, cancellationToken);
        return [.. (await store.ListGrantsAsync(query.SiteId, cancellationToken))
            .Where(g => (query.PersonId is null || g.Grant.PersonId == query.PersonId) && (query.QualificationId is null || g.Grant.QualificationId == query.QualificationId))
            .Select(g => QualificationView.ToDto(g, today, now))
            .Where(g => query.ExpiringWithinDays is not { } days
                || (g.Status == GrantStatus.Valid && g.ExpiresOn is { } e && e <= today.AddDays(days)))
            .OrderBy(g => g.PersonName, StringComparer.Ordinal).ThenBy(g => g.QualificationCode, StringComparer.Ordinal).ThenByDescending(g => g.ValidFrom)];
    }
}

internal sealed class GetEquipmentQualificationsHandler(IQualificationReadStore store) : IQueryHandler<GetEquipmentQualificationsQuery, EquipmentQualificationsDto>
{
    public async Task<EquipmentQualificationsDto> HandleAsync(GetEquipmentQualificationsQuery query, CancellationToken cancellationToken)
    {
        var rows = await store.ListRequirementsAsync(query.EquipmentId, cancellationToken) ?? throw new NotFoundException($"Equipment {query.EquipmentId} not found.");
        static EquipmentQualificationDto Dto(RequirementRow r) => new(r.QualificationId, r.Code, r.Name, r.FromEquipmentId, r.FromEquipmentCode);
        return new EquipmentQualificationsDto
        {
            EquipmentId = query.EquipmentId,
            Own = [.. rows.Where(r => r.IsOwn).Select(Dto)],
            Inherited = [.. rows.Where(r => !r.IsOwn).Select(Dto)],
        };
    }
}
