using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Security;

namespace Pulaski.Application.EquipmentModel;

/// <summary>How much of a site's reference data there is to copy (#140b).</summary>
public sealed record SiteCopyCounts(
    int ShiftSchedules, int CalendarExceptions, int Reasons, int MachineProfiles, int Crews, int Qualifications, int AndonTypes, int LotAttributes, int Settings);

public interface ISiteCopyRepository
{
    Task<SiteCopyCounts> CountAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Shift schedules (their one-day changes left out) and calendar exceptions still to come, at the site itself.</summary>
    Task<int> CopyShiftsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The site's reasons, groups before their members, then machine profiles with their maps, reasons pointing at the copies.</summary>
    Task<int> CopyReasonsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Crews, qualifications and andon call types.</summary>
    Task<int> CopyPeopleListsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The settings rows, the site's OEE targets (from today) and lot attributes with their classes.</summary>
    Task<int> CopySettingsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>What copying a site's reference data would bring (#140b).</summary>
public sealed record GetSiteCopyPlanQuery(Guid SiteId) : IQuery<SiteCopyPlanDto>;

/// <summary>Makes a new site beside the original, under the same enterprise, with its reference data (#140b, D-132), in one transaction.</summary>
public sealed record CopySiteCommand(UserAccess Caller, Guid SiteId, CopySiteRequest Request) : ICommand<CreatedResponse>;

internal sealed class GetSiteCopyPlanHandler(IUnitOfWork unitOfWork, IEquipmentRepository equipment, ISiteCopyRepository copies, TimeProvider time)
    : IQueryHandler<GetSiteCopyPlanQuery, SiteCopyPlanDto>
{
    public async Task<SiteCopyPlanDto> HandleAsync(GetSiteCopyPlanQuery query, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var site = (await equipment.GetAsync(query.SiteId, RowLock.Share, cancellationToken))?.Value;
        if (site is not { Level: "site" })
        {
            throw new NotFoundException($"Site {query.SiteId} not found.");
        }

        var c = await copies.CountAsync(site.Id, time.GetUtcNow(), cancellationToken);
        return new SiteCopyPlanDto
        {
            SourceSiteId = site.Id,
            SourceCode = site.Code,
            SourceTimeZone = site.TimeZone,
            ShiftSchedules = c.ShiftSchedules,
            CalendarExceptions = c.CalendarExceptions,
            Reasons = c.Reasons,
            MachineProfiles = c.MachineProfiles,
            Crews = c.Crews,
            Qualifications = c.Qualifications,
            AndonTypes = c.AndonTypes,
            LotAttributes = c.LotAttributes,
            Settings = c.Settings,
        };
    }
}

internal sealed class CopySiteHandler(
    IUnitOfWork unitOfWork, IEquipmentRepository equipment, ISiteCopyRepository copies, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CopySiteCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CopySiteCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!command.Caller.CanEverywhere(Permissions.Equipment))
        {
            throw new ForbiddenException($"Making a site needs permission to manage equipment everywhere.");
        }

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone.Trim();
        new Validator()
            .Require(!string.IsNullOrWhiteSpace(request.Code), nameof(request.Code), "Give the new site a code.")
            .Require(!string.IsNullOrWhiteSpace(request.Name), nameof(request.Name), "Give the new site a name.")
            .Require(timeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _), nameof(request.TimeZone), "Choose the new site's time zone.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var source = (await equipment.GetAsync(command.SiteId, RowLock.Share, cancellationToken))?.Value;
        if (source is not { Level: "site", ParentId: { } enterpriseId })
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        var enterprise = (await equipment.GetAsync(enterpriseId, RowLock.Share, cancellationToken))!.Value;
        var site = Equipment.Create(ids.NewId(), enterprise, request.Code!.Trim(), request.Name!.Trim(), source.Description, "site", "site", false, false, false, timeZone);
        var created = events.Create(EventTypes.EquipmentCreated, "equipment", site.Id, new { After = site, CopiedFrom = source.Id });
        await equipment.InsertAsync(site, created.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(created, cancellationToken);

        var at = created.OccurredAt;
        var copied = new Dictionary<string, int>(StringComparer.Ordinal);
        if (request.Shifts)
        {
            copied["shifts"] = await copies.CopyShiftsAsync(source.Id, site.Id, ids.NewId, at, cancellationToken);
        }

        if (request.Reasons)
        {
            copied["reasons"] = await copies.CopyReasonsAsync(source.Id, site.Id, ids.NewId, at, cancellationToken);
        }

        if (request.People)
        {
            copied["people"] = await copies.CopyPeopleListsAsync(source.Id, site.Id, ids.NewId, at, cancellationToken);
        }

        if (request.Settings)
        {
            copied["settings"] = await copies.CopySettingsAsync(source.Id, site.Id, ids.NewId, at, cancellationToken);
        }

        await eventLog.AppendAsync(events.Create(EventTypes.SiteCopied, "equipment", site.Id, new { From = source.Id, FromCode = source.Code, Copied = copied }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(site.Id, 0);
    }
}
