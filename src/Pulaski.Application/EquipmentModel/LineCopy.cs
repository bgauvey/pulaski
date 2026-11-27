using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Localization;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Security;

namespace Pulaski.Application.EquipmentModel;

/// <summary>A piece of the line to copy, as stored (#140).</summary>
public sealed record LineCopySource(Guid Id, Guid? ParentId, int Depth, string Code, string Name, string Level, string Role);

/// <summary>The line to copy: its pieces (the line first, then below it by depth) and how much comes with them.</summary>
public sealed record LineCopySourceLine(
    Guid LineId, string Code, Guid SiteId, Guid? ParentId, bool CanRunJob, IReadOnlyList<LineCopySource> Pieces, int Capabilities, int ChangeoverRules,
    int StopCodeOverrides, int Qualifications, int OeeTargets, int MaintenancePlans, int InstructionTargets, int Tags);

/// <summary>One copy to make: the original's id, the new id and parent, and its code and name.</summary>
public sealed record LineCopyRow(Guid SourceId, Guid NewId, Guid NewParentId, string Code, string Name);

public interface ILineCopyRepository
{
    Task<LineCopySourceLine?> LoadAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>Every code in use at the site, ignoring case.</summary>
    Task<IReadOnlySet<string>> CodesAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site of <paramref name="parentId"/> when it can hold a line (a site or an area), locked; null otherwise.</summary>
    Task<Guid?> LockParentAsync(Guid parentId, CancellationToken cancellationToken);

    /// <summary>
    /// Into another site (#140c): what the line uses that the target site has nothing of by code (machine profile, shift schedule, label printer,
    /// output location, qualifications, stop-code reasons, work instructions), for the review.
    /// </summary>
    Task<IReadOnlyList<(string Kind, string Code)>> UnmatchedAsync(Guid lineId, Guid targetSiteId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the copies (parents first) at <paramref name="targetSiteId"/>, with the tree; machine settings and links inside the copy follow when
    /// asked. Site-owned references (profile, schedule, printer, an output location outside the line) are found at the target site by code.
    /// </summary>
    Task CopyEquipmentAsync(IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, bool machineSettings, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Copies capabilities in force (from now, each with its planning rate from the ideal rate) and the line's changeover rules.</summary>
    Task<IReadOnlyList<Guid>> CopyCapabilitiesAsync(IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Copies stop-code overrides (machine settings), a site's own reason found at the target site by kind and code.</summary>
    Task CopyStopCodesAsync(IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, CancellationToken cancellationToken);

    /// <summary>Copies qualifications required, OEE targets in force (from today), maintenance plans (started now) and instructions aimed at a copied line.</summary>
    Task CopyRequirementsAsync(IReadOnlyList<LineCopyRow> rows, Guid targetSiteId, Func<Guid> newId, DateTimeOffset at, string by, CancellationToken cancellationToken);
}

/// <summary>What copying <paramref name="LineId"/> as <paramref name="NewCode"/> would make (#140), for the review.</summary>
public sealed record GetLineCopyPlanQuery(Guid LineId, string? NewCode, string? NewName, Guid? ParentId = null) : IQuery<LineCopyPlanDto>;

/// <summary>Copies a line and what comes with it (#140, D-132), in one transaction; the caller's permissions decide which parts may come.</summary>
public sealed record CopyLineCommand(UserAccess Caller, Guid LineId, CopyLineRequest Request) : ICommand<CreatedResponse>;

internal sealed class GetLineCopyPlanHandler(IUnitOfWork unitOfWork, ILineCopyRepository copies) : IQueryHandler<GetLineCopyPlanQuery, LineCopyPlanDto>
{
    public async Task<LineCopyPlanDto> HandleAsync(GetLineCopyPlanQuery query, CancellationToken cancellationToken)
    {
        // Read as the copy will be: the same reads, in a transaction that writes nothing and is rolled back.
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var line = await copies.LoadAsync(query.LineId, cancellationToken) ?? throw new NotFoundException($"Line {query.LineId} not found.");
        var newCode = string.IsNullOrWhiteSpace(query.NewCode) ? $"{line.Code}-COPY" : query.NewCode.Trim();
        var newName = string.IsNullOrWhiteSpace(query.NewName) ? $"{line.Pieces[0].Name} (copy)" : query.NewName.Trim();
        var items = line.Pieces.Select(p => new LineCopyItemDto
        {
            SourceId = p.Id,
            SourceParentId = p.Id == line.LineId ? null : p.ParentId,
            Depth = p.Depth,
            SourceCode = p.Code,
            SourceName = p.Name,
            Level = p.Level,
            Role = p.Role,
            Code = p.Id == line.LineId ? newCode : LineCopyRules.ChildCode(line.Code, newCode, p.Code),
            Name = p.Id == line.LineId ? newName : p.Name,
        }).ToList();
        var target = query.ParentId is { } parent
            ? await copies.LockParentAsync(parent, cancellationToken) ?? throw new DomainRuleException($"A copied line goes under a site or an area.")
            : line.SiteId;
        var problems = LineCopyRules.Problems([.. items.Select(i => new LineCopyItem(i.SourceId, i.SourceParentId, i.Depth, i.Code, i.Name))],
            await copies.CodesAsync(target, cancellationToken));
        var unmatched = target == line.SiteId ? [] : await copies.UnmatchedAsync(line.LineId, target, cancellationToken);
        return new LineCopyPlanDto
        {
            SourceLineId = line.LineId,
            SourceCode = line.Code,
            SiteId = line.SiteId,
            ParentId = query.ParentId ?? line.ParentId,
            TargetSiteId = target,
            Unmatched = [.. unmatched.Select(u => Notes.Of($"{UserMessage.Code(u.Kind)} {u.Code} isn't at that site, so the copy goes without it."))],
            Items = items,
            Capabilities = line.Capabilities,
            ChangeoverRules = line.ChangeoverRules,
            StopCodeOverrides = line.StopCodeOverrides,
            Qualifications = line.Qualifications,
            OeeTargets = line.OeeTargets,
            MaintenancePlans = line.MaintenancePlans,
            InstructionTargets = line.InstructionTargets,
            TagsLeftOut = line.Tags,
            Problems = [.. problems.Select(Notes.Of)],
        };
    }
}

internal sealed class CopyLineHandler(IUnitOfWork unitOfWork, ILineCopyRepository copies, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CopyLineCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CopyLineCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var line = await copies.LoadAsync(command.LineId, cancellationToken) ?? throw new NotFoundException($"Line {command.LineId} not found.");
        if (!line.CanRunJob)
        {
            throw new DomainRuleException($"Only a line that runs jobs can be copied.");
        }

        var parentId = request.ParentId ?? line.ParentId ?? throw new DomainRuleException($"Choose where the copy goes.");
        var target = await copies.LockParentAsync(parentId, cancellationToken) ?? throw new DomainRuleException($"A copied line goes under a site or an area.");

        // The copy is made at the target site, so the caller's permissions there decide (#140c); seeing the original is enough of it.
        var caller = command.Caller;
        if (!caller.Can(Permissions.Equipment, target) || !caller.CanAny([Permissions.EquipmentView, Permissions.Equipment], line.SiteId))
        {
            throw new ForbiddenException($"Copying a line needs permission to manage equipment where the copy goes.");
        }

        if (request.Capabilities && !caller.Can(Permissions.Processes, target))
        {
            throw new ForbiddenException($"Copying capabilities needs permission to maintain processes and capabilities where the copy goes.");
        }

        // The pieces to make, parents first; without the equipment part, only the line itself.
        var pieces = request.Equipment ? line.Pieces : [line.Pieces[0]];
        var names = request.Items.ToDictionary(i => i.SourceId);
        var newIds = pieces.ToDictionary(p => p.Id, _ => ids.NewId());
        var rows = pieces.Select(p => new LineCopyRow(
            p.Id, newIds[p.Id], p.Id == line.LineId ? parentId : newIds[p.ParentId!.Value],
            (names.GetValueOrDefault(p.Id)?.Code ?? (p.Id == line.LineId ? "" : p.Code)).Trim(),
            (names.GetValueOrDefault(p.Id)?.Name ?? p.Name).Trim())).ToList();
        var problems = LineCopyRules.Problems([.. rows.Select(r => new LineCopyItem(r.SourceId, null, 0, r.Code, r.Name))], await copies.CodesAsync(target, cancellationToken));
        if (problems.Count > 0)
        {
            throw new DomainRuleException(problems[0]);
        }

        var envelope = events.Create(EventTypes.LineCopied, "equipment", newIds[line.LineId], new
        {
            From = line.LineId,
            FromCode = line.Code,
            FromSite = line.SiteId,
            ToSite = target,
            Pieces = rows.Select(r => new { r.SourceId, r.NewId, r.Code }),
            request.Equipment,
            request.Capabilities,
            request.MachineSettings,
            request.Requirements,
        });
        await copies.CopyEquipmentAsync(rows, target, request.MachineSettings, ids.NewId, envelope.OccurredAt, cancellationToken);
        foreach (var row in rows)
        {
            await eventLog.AppendAsync(events.Create(EventTypes.EquipmentCreated, "equipment", row.NewId, new { row.Code, row.Name, CopiedFrom = row.SourceId }), cancellationToken);
        }

        if (request.Capabilities)
        {
            foreach (var capability in await copies.CopyCapabilitiesAsync(rows, target, ids.NewId, envelope.OccurredAt, cancellationToken))
            {
                await eventLog.AppendAsync(events.Create(EventTypes.CapabilityAdded, "equipment_capability", capability, new { CopiedWith = newIds[line.LineId] }), cancellationToken);
            }
        }

        if (request.MachineSettings)
        {
            await copies.CopyStopCodesAsync(rows, target, cancellationToken);
        }

        if (request.Requirements)
        {
            await copies.CopyRequirementsAsync(rows, target, ids.NewId, envelope.OccurredAt, envelope.ActorId, cancellationToken);
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(newIds[line.LineId], 0);
    }
}
