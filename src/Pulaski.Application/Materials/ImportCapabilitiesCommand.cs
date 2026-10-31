using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Materials;

/// <summary>
/// Loads capabilities in bulk (D-055), all or nothing like the materials import (D-034). Each row is a material on a line: the
/// capability in effect now is changed in place (the run standard follows its ideal rate as on the material page), a new one is added
/// from now when there is none, and <c>active = no</c> ends it. Capabilities are never deleted, and future or past periods are left
/// alone. Every line the file names is locked first, in id order, then the materials (D-017).
/// </summary>
/// <param name="Rows">The rows, and any problem with the file as a whole.</param>
/// <param name="DryRun">True to check the rows and report what would happen, saving nothing.</param>
/// <param name="Source">What the rows came from, such as "CSV import"; recorded as each event's reason.</param>
/// <param name="Sites">The sites whose lines the caller may change; null for every site.</param>
public sealed record ImportCapabilitiesCommand(CapabilityImportSource Rows, bool DryRun, string Source, IReadOnlyCollection<Guid>? Sites)
    : ICommand<ImportMaterialsResponse>;

internal sealed class ImportCapabilitiesHandler(
    IUnitOfWork unitOfWork,
    ICapabilityRepository capabilities,
    IMaterialAdminRepository materials,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ImportCapabilitiesCommand, ImportMaterialsResponse>
{
    private const string Create = "create";
    private const string Update = "update";
    private const string Unchanged = "unchanged";
    private const string Error = "error";

    public async Task<ImportMaterialsResponse> HandleAsync(ImportCapabilitiesCommand command, CancellationToken cancellationToken)
    {
        if (command.Rows.Problems.Count > 0)
        {
            return new ImportMaterialsResponse { FileProblems = command.Rows.Problems, Errors = command.Rows.Problems.Count };
        }

        var now = timeProvider.GetUtcNow();
        var writes = new CapabilityWrites(capabilities, materials, units, eventLog, events, ids);
        var results = new List<ImportRowResultDto>();
        var pairs = new HashSet<(string, Guid)>();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lines = await capabilities.LockLinesByCodeAsync(
            [.. command.Rows.Rows.Select(r => r.LineCode.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct()], cancellationToken);

        foreach (var row in command.Rows.Rows)
        {
            var label = $"{row.MaterialCode.Trim()} on {row.LineCode.Trim()}";
            try
            {
                if (row.Problems.Count > 0)
                {
                    throw new DomainRuleException(string.Join(" ", row.Problems));
                }

                results.Add(await ImportRowAsync(row, label, lines, pairs, command, writes, now, cancellationToken));
            }
            catch (Exception ex) when (ex is DomainRuleException or ValidationException or NotFoundException)
            {
                results.Add(new ImportRowResultDto { Line = row.Line, Code = label, Action = Error, Message = Message(ex) });
            }
        }

        var errors = results.Count(r => r.Action == Error);
        var apply = errors == 0 && !command.DryRun;
        if (apply)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ImportMaterialsResponse
        {
            Applied = apply,
            Created = results.Count(r => r.Action == Create),
            Updated = results.Count(r => r.Action == Update),
            Unchanged = results.Count(r => r.Action == Unchanged),
            Errors = errors,
            Rows = results,
        };
    }

    private async Task<ImportRowResultDto> ImportRowAsync(
        CapabilityImportRow row,
        string label,
        IReadOnlyList<SiteLine> lines,
        HashSet<(string, Guid)> pairs,
        ImportCapabilitiesCommand command,
        CapabilityWrites writes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var materialCode = row.MaterialCode.Trim();
        var ending = row.IsActive?.Value == false;
        new Validator()
            .RequireText(materialCode, "material", MaterialRules.MaxCode)
            .RequireText(row.LineCode, "line", 50)
            .Require(ending || row.IdealRatePerHour is not null, "ideal_rate", "Required, unless the row ends the capability (active = no).")
            .Require(ending || !string.IsNullOrWhiteSpace(row.RateUnit), "rate_unit", "Required, unless the row ends the capability (active = no).")
            .ThrowIfInvalid();

        var line = Line(row, lines);
        if (command.Sites is { } sites && (line.SiteId is not { } siteId || !sites.Contains(siteId)))
        {
            throw new DomainRuleException($"You can't change the capabilities of {line.Line.Code} at {line.SiteCode ?? "its site"}.");
        }

        var material = (await materials.FindByCodeForUpdateAsync(materialCode, cancellationToken))?.Value
            ?? throw new DomainRuleException($"There is no material {materialCode}.");
        if (!pairs.Add((material.Code, line.Line.Id)))
        {
            throw new DomainRuleException($"{label} appears earlier in the file.");
        }

        var current = (await capabilities.ListOnLineAsync(line.Line.Id, material.Id, cancellationToken)).FirstOrDefault(c => c.IsInEffect(now));
        if (ending)
        {
            return await EndAsync(row, label, current, line.Line, material, command.Source, writes, now, cancellationToken);
        }

        var unit = await materials.FindUomAsync(row.RateUnit.Trim(), cancellationToken)
            ?? throw new DomainRuleException($"There is no unit '{row.RateUnit.Trim()}' in Pulaski.");
        int? Minutes(TimeSpan? changeover) => changeover is { } c ? (int)c.TotalMinutes : null;
        var change = CapabilityWrites.Change(
            row.IdealRatePerHour!.Value,
            unit.Id,
            row.ChangeoverMinutes is { } minutes ? minutes.Value : Minutes(current?.StandardChangeover),
            row.CrewSize is { } crew ? crew.Value : current?.StandardCrewSize,
            row.IsPreferred?.Value ?? current?.IsPreferred ?? false,
            current?.EffectiveFrom ?? now,
            current?.EffectiveTo,
            row.BatchSize is { } batch ? batch.Value : current?.BatchSize,
            row.BatchTolerancePct is { } tolerance ? tolerance.Value : current?.BatchTolerancePct);
        await writes.MaterialForRateAsync(material.Id, unit.Id, current?.RateUomId, cancellationToken);
        var unitCode = await AddCapabilityHandler.UnitCodeAsync(units, unit.Id, cancellationToken);

        if (current is null)
        {
            if (!line.Line.CanRunJob || !line.Line.IsActive)
            {
                throw new DomainRuleException($"{line.Line.Code} {(line.Line.IsActive ? "doesn't run jobs" : "is inactive")}; a capability goes on an active line that runs jobs.");
            }

            if (!material.IsActive)
            {
                throw new DomainRuleException($"{material.Code} is inactive; switch it on before adding a line that can run it.");
            }

            var added = Capability.Create(ids.NewId(), line.Line.Id, material.Id, change);
            await writes.EnsureNoOverlapAsync(added, line.Line.Code, material.Code, cancellationToken);
            await writes.CheckBatchSizeAsync(added, null, material, line.Line.Code, cancellationToken);
            var envelope = events.Create(EventTypes.CapabilityAdded, "equipment_capability", added.Id, new { After = added }, command.Source);
            await capabilities.InsertAsync(added, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            var note = await writes.FollowIdealRateAsync(added, unitCode, now, cancellationToken);
            return new ImportRowResultDto { Line = row.Line, Code = label, Action = Create, Notes = note is null ? [] : [note] };
        }

        var updated = current.Edit(change);
        if (updated == current)
        {
            return new ImportRowResultDto { Line = row.Line, Code = label, Action = Unchanged };
        }

        await writes.CheckBatchSizeAsync(updated, current, material, line.Line.Code, cancellationToken);
        var changed = events.Create(EventTypes.CapabilityChanged, "equipment_capability", updated.Id, new { Before = current, After = updated }, command.Source);
        await capabilities.UpdateAsync(updated, cancellationToken);
        await eventLog.AppendAsync(changed, cancellationToken);
        var notes = new List<string>();
        if (updated.IdealRatePerHour != current.IdealRatePerHour || updated.RateUomId != current.RateUomId)
        {
            notes.AddIfSet(await writes.FollowIdealRateAsync(updated, unitCode, now, cancellationToken));
        }

        return new ImportRowResultDto { Line = row.Line, Code = label, Action = Update, Changes = Changes(current, updated), Notes = notes };
    }

    private async Task<ImportRowResultDto> EndAsync(
        CapabilityImportRow row, string label, Capability? current, CapabilityLine line, Material material, string source,
        CapabilityWrites writes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (current is null)
        {
            return new ImportRowResultDto { Line = row.Line, Code = label, Action = Unchanged, Notes = ["No capability in effect to end."] };
        }

        var ended = current.End(now);
        var envelope = events.Create(EventTypes.CapabilityEnded, "equipment_capability", ended.Id, new { Before = current, After = ended }, source);
        await capabilities.UpdateAsync(ended, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        var note = await writes.OpenJobsLeftAsync(current, ended, line.Code, material.Code, now, cancellationToken);
        return new ImportRowResultDto { Line = row.Line, Code = label, Action = Update, Changes = ["ended"], Notes = note is null ? [] : [note] };
    }

    /// <summary>The line the row names: its code at the row's site, or at the only site that has it.</summary>
    private static SiteLine Line(CapabilityImportRow row, IReadOnlyList<SiteLine> lines)
    {
        var code = row.LineCode.Trim();
        var site = row.SiteCode?.Trim();
        var matches = lines
            .Where(l => string.Equals(l.Line.Code, code, StringComparison.OrdinalIgnoreCase)
                && (site is null || string.Equals(l.SiteCode, site, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new DomainRuleException($"There is no line {code}{(site is null ? "" : $" at {site}")}."),
            _ => throw new DomainRuleException(
                $"{code} is at several sites ({string.Join(", ", matches.Select(m => m.SiteCode))}); give the site."),
        };
    }

    private static List<string> Changes(Capability before, Capability after)
    {
        var changes = new List<string>();
        void Add(bool changed, string field)
        {
            if (changed)
            {
                changes.Add(field);
            }
        }

        Add(before.IdealRatePerHour != after.IdealRatePerHour, "ideal_rate");
        Add(before.RateUomId != after.RateUomId, "rate_unit");
        Add(before.StandardChangeover != after.StandardChangeover, "changeover_minutes");
        Add(before.StandardCrewSize != after.StandardCrewSize, "crew_size");
        Add(before.IsPreferred != after.IsPreferred, "preferred");
        Add(before.BatchSize != after.BatchSize, "batch_size");
        Add(before.BatchTolerancePct != after.BatchTolerancePct, "batch_tolerance");
        return changes;
    }

    private static string Message(Exception ex) => ex switch
    {
        ValidationException v => string.Join(" ", v.Errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))),
        _ => ex.Message,
    };
}
