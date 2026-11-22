using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;

namespace Pulaski.Application.Execution;

/// <summary>A line's report defaults (#122, D-111): where its good output goes unless the report says otherwise, and its lots' label printer.</summary>
public sealed record LineOutput(Guid EquipmentId, string Code, Guid SiteId, bool CanRunJob, Guid? OutputLocationId, Guid? LabelPrinterId);

/// <summary>Line output writes (#122), inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ILineOutputRepository
{
    Task<Versioned<LineOutput>?> GetForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken);

    /// <summary>Whether it is an active location of the site that holds inventory.</summary>
    Task<bool> IsLocationAsync(Guid siteId, Guid locationId, CancellationToken cancellationToken);

    /// <summary>Whether it is an active label printer of the site.</summary>
    Task<bool> IsPrinterAsync(Guid siteId, Guid printerId, CancellationToken cancellationToken);

    Task<long> UpdateAsync(LineOutput output, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>
/// Prints the labels of lots a line closed (#122, D-111) on the line's label printer, after the change that closed them has committed. Never
/// fails what printed them: what happened comes back as notes. The API prints; elsewhere (tests, tools) nothing is printed.
/// </summary>
public interface ILineLabels
{
    Task<IReadOnlyList<InventoryWarningDto>> PrintClosedLotsAsync(Guid lineId, IReadOnlyList<Guid> lotIds, CancellationToken cancellationToken);
}

internal sealed class NoLineLabels : ILineLabels
{
    public Task<IReadOnlyList<InventoryWarningDto>> PrintClosedLotsAsync(Guid lineId, IReadOnlyList<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InventoryWarningDto>>([]);
}

/// <summary>Sets a line's default output location and label printer (#122); null clears either.</summary>
public sealed record SetLineOutputCommand(Guid EquipmentId, long ExpectedVersion, Guid? OutputLocationId, Guid? LabelPrinterId) : ICommand<VersionResponse>;

internal sealed class SetLineOutputHandler(IUnitOfWork unitOfWork, ILineOutputRepository lines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetLineOutputCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetLineOutputCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await lines.GetForUpdateAsync(command.EquipmentId, cancellationToken) ?? throw new NotFoundException($"Equipment {command.EquipmentId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var line = current.Value;
        if (!line.CanRunJob)
        {
            throw new DomainRuleException($"{line.Code} doesn't run jobs, so nothing is reported there.");
        }

        if (command.OutputLocationId is { } location && location != line.OutputLocationId && !await lines.IsLocationAsync(line.SiteId, location, cancellationToken))
        {
            throw new DomainRuleException($"Choose an active location of {line.Code}'s site that holds inventory.");
        }

        if (command.LabelPrinterId is { } printer && printer != line.LabelPrinterId && !await lines.IsPrinterAsync(line.SiteId, printer, cancellationToken))
        {
            throw new DomainRuleException($"Choose an active label printer of {line.Code}'s site.");
        }

        var updated = line with { OutputLocationId = command.OutputLocationId, LabelPrinterId = command.LabelPrinterId };
        if (updated == line)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.LineOutputSet, "equipment", line.EquipmentId, new { Before = line, After = updated });
        var version = await lines.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
