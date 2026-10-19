using System.Globalization;
using System.Text.RegularExpressions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

public sealed record CreateEdgeNodeCommand(Guid SiteId, string Code, string Name, string? Description) : ICommand<CreatedResponse>;

public sealed record UpdateEdgeNodeCommand(Guid Id, long ExpectedVersion, string Name, string? Description, bool IsActive) : ICommand<VersionResponse>;
public sealed record DeleteEdgeNodeCommand(Guid Id) : ICommand<bool>;

public sealed record CreateEdgeConnectionCommand(Guid EdgeNodeId, string Code, string Name, string Protocol, IReadOnlyDictionary<string, string> Settings)
    : ICommand<CreatedResponse>;

public sealed record UpdateEdgeConnectionCommand(Guid Id, long ExpectedVersion, string Name, IReadOnlyDictionary<string, string> Settings, bool IsActive)
    : ICommand<VersionResponse>;

/// <summary>Binds a signal the edge reads to a role on a piece of equipment (D-041).</summary>
public sealed record CreateEquipmentTagCommand(
    Guid EquipmentId, string Role, Guid ConnectionId, string Address, decimal ScaleFactor, decimal ScaleOffset, long? CounterRollover)
    : ICommand<CreatedResponse>;

public sealed record UpdateEquipmentTagCommand(
    Guid Id, long ExpectedVersion, string Address, decimal ScaleFactor, decimal ScaleOffset, long? CounterRollover, bool IsActive)
    : ICommand<VersionResponse>;
public sealed record DeleteEquipmentTagCommand(Guid Id) : ICommand<bool>;

/// <summary>Connector protocols, their settings and their addresses (D-041).</summary>
public static partial class EdgeProtocols
{
    public const string SparkplugB = "sparkplug_b";

    public static IReadOnlyList<string> All { get; } = [SparkplugB];

    /// <summary>The settings a Sparkplug B connection takes; the broker password stays in the edge's own configuration.</summary>
    public static IReadOnlyList<string> SparkplugSettings { get; } = ["host", "port", "tls", "clientId", "groupId", "hostId", "username"];

    /// <summary>What is wrong with a connection's settings, field by field; empty when nothing is.</summary>
    public static IReadOnlyList<string> SettingsProblems(string protocol, IReadOnlyDictionary<string, string> settings)
    {
        var problems = new List<string>();
        if (protocol != SparkplugB)
        {
            return ["Only Sparkplug B connections are supported so far."];
        }

        problems.AddRange(settings.Keys.Where(k => !SparkplugSettings.Contains(k)).Select(k => $"'{k}' isn't a Sparkplug B setting ({string.Join(", ", SparkplugSettings)})."));
        if (!settings.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            problems.Add("Give the broker's host.");
        }

        if (settings.TryGetValue("port", out var port) && !(int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536))
        {
            problems.Add("The port is a number from 1 to 65535.");
        }

        if (settings.TryGetValue("tls", out var tls) && tls is not ("true" or "false"))
        {
            problems.Add("tls is true or false.");
        }

        if (settings.TryGetValue("groupId", out var group) && !SparkplugId().IsMatch(group))
        {
            problems.Add("The group id has no '/', '+', '#' or ':'.");
        }

        problems.AddRange(settings.Where(s => s.Value.Length > 200).Select(s => $"{s.Key} is at most 200 characters."));
        return problems;
    }

    /// <summary>A Sparkplug B address: <c>group/node[/device]:metric</c>.</summary>
    public static bool IsValidAddress(string protocol, string address) => protocol == SparkplugB && SparkplugAddress().IsMatch(address);

    [GeneratedRegex(@"^[^/+#:]+$")]
    private static partial Regex SparkplugId();

    [GeneratedRegex(@"^[^/+#:]+/[^/+#:]+(/[^/+#:]+)?:.{1,300}$")]
    private static partial Regex SparkplugAddress();
}

internal static class EdgeRules
{
    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static IReadOnlyDictionary<string, string> Clean(IReadOnlyDictionary<string, string> settings) =>
        settings.Where(s => !string.IsNullOrWhiteSpace(s.Value)).ToDictionary(s => s.Key.Trim(), s => s.Value.Trim(), StringComparer.Ordinal);

    public static void EnsureSettings(string protocol, IReadOnlyDictionary<string, string> settings)
    {
        if (EdgeProtocols.SettingsProblems(protocol, settings) is { Count: > 0 } problems)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Settings"] = [.. problems] });
        }
    }

    public static void EnsureTagNumbers(decimal scaleFactor, long? counterRollover) => new Validator()
        .Require(scaleFactor != 0, "ScaleFactor", "The scale factor isn't zero.")
        .Require(counterRollover is null or > 0, "CounterRollover", "A rollover value is above zero.")
        .ThrowIfInvalid();

    public static async Task<EdgeNodeRecord> LockNodeAsync(IEdgeRepository edge, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var current = await edge.GetNodeForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Edge node {id} not found.");
        if (expectedVersion is { } version && current.Version != version)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }
}

internal sealed class DeleteEdgeNodeHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<DeleteEdgeNodeCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteEdgeNodeCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var node = await EdgeRules.LockNodeAsync(edge, command.Id, null, cancellationToken);
        var connections = await edge.ListConnectionsForUpdateAsync(node.Id, cancellationToken);
        var tags = await edge.ListTagsForUpdateAsync(connections.Select(c => c.Value.Id).ToArray(), cancellationToken);

        foreach (var current in tags.Where(t => t.Value.IsActive))
        {
            var updated = current.Value with { IsActive = false };
            var envelope = events.Create(EventTypes.EquipmentTagDeactivated, "equipment", updated.EquipmentId, new { Before = current.Value, After = updated });
            await edge.UpdateTagAsync(updated, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
        }

        foreach (var current in connections.Where(c => c.Value.IsActive))
        {
            var updated = current.Value with { IsActive = false };
            var envelope = events.Create(EventTypes.EdgeConnectionDeleted, "edge_node", node.Id, new { Before = current.Value, After = updated });
            await edge.UpdateConnectionAsync(updated, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
        }

        if (node.IsActive)
        {
            var updated = node with { IsActive = false };
            var envelope = events.Create(EventTypes.EdgeNodeDeleted, "edge_node", node.Id, new { Before = node, After = updated });
            await edge.UpdateNodeAsync(updated, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

internal sealed class DeleteEquipmentTagHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<DeleteEquipmentTagCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteEquipmentTagCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var snapshot = await edge.GetTagAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Tag {command.Id} not found.");
        var connectionSnapshot = await edge.GetConnectionAsync(snapshot.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"Connection {snapshot.ConnectionId} not found.");
        await EdgeRules.LockNodeAsync(edge, connectionSnapshot.EdgeNodeId, null, cancellationToken);
        _ = await edge.GetConnectionForUpdateAsync(snapshot.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"Connection {snapshot.ConnectionId} not found.");
        var current = await edge.GetTagForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Tag {command.Id} not found.");
        var detachedQualitySources = await edge.DetachTagQualitySourcesAsync(command.Id, cancellationToken);
        var envelope = events.Create(EventTypes.EquipmentTagDeleted, "equipment", current.Value.EquipmentId,
            new { Before = current.Value, DetachedQualityCharacteristicIds = detachedQualitySources });
        if (await edge.DeleteTagAsync(command.Id, cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Tag {command.Id} was not deleted.");
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

internal sealed class CreateEdgeNodeHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateEdgeNodeCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateEdgeNodeCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        var description = EdgeRules.Optional(command.Description);
        new Validator()
            .Require(MachineProfileRules.IsValidCode(code), "Code", "Letters, digits, '-', '_' or '.', up to 20.")
            .RequireText(name, "Name", 100)
            .Require(description is null || description.Length <= 500, "Description", "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await edge.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("An edge node belongs to a site; choose one.");
        }

        if (await edge.NodeCodeExistsAsync(command.SiteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has an edge node {code}.");
        }

        var node = new EdgeNodeRecord(ids.NewId(), command.SiteId, code, name, description, IsActive: true, null, 0, null, null);
        var envelope = events.Create(EventTypes.EdgeNodeCreated, "edge_node", node.Id, new { After = node });
        var version = await edge.InsertNodeAsync(node, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(node.Id, version);
    }
}

internal sealed class UpdateEdgeNodeHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateEdgeNodeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateEdgeNodeCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        var description = EdgeRules.Optional(command.Description);
        new Validator()
            .RequireText(name, "Name", 100)
            .Require(description is null || description.Length <= 500, "Description", "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await EdgeRules.LockNodeAsync(edge, command.Id, command.ExpectedVersion, cancellationToken);
        var updated = current with { Name = name, Description = description, IsActive = command.IsActive };
        if (updated == current)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        var envelope = events.Create(EventTypes.EdgeNodeUpdated, "edge_node", updated.Id, new { Before = current, After = updated });
        var version = await edge.UpdateNodeAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class CreateEdgeConnectionHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateEdgeConnectionCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateEdgeConnectionCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        var settings = EdgeRules.Clean(command.Settings);
        new Validator()
            .Require(MachineProfileRules.IsValidCode(code), "Code", "Letters, digits, '-', '_' or '.', up to 20.")
            .RequireText(name, "Name", 100)
            .Require(EdgeProtocols.All.Contains(command.Protocol), "Protocol", "Sparkplug B.")
            .ThrowIfInvalid();
        EdgeRules.EnsureSettings(command.Protocol, settings);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var node = await EdgeRules.LockNodeAsync(edge, command.EdgeNodeId, null, cancellationToken);
        if (await edge.ConnectionCodeExistsAsync(node.Id, code, cancellationToken))
        {
            throw new ConflictException($"{node.Code} already has a connection {code}.");
        }

        var connection = new EdgeConnectionRecord(ids.NewId(), node.Id, code, name, command.Protocol, settings, IsActive: true);
        var envelope = events.Create(EventTypes.EdgeConnectionCreated, "edge_node", node.Id, new { After = connection });
        var version = await edge.InsertConnectionAsync(connection, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(connection.Id, version);
    }
}

internal sealed class UpdateEdgeConnectionHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateEdgeConnectionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateEdgeConnectionCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        var settings = EdgeRules.Clean(command.Settings);
        new Validator().RequireText(name, "Name", 100).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var snapshot = await edge.GetConnectionAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Connection {command.Id} not found.");
        await EdgeRules.LockNodeAsync(edge, snapshot.EdgeNodeId, null, cancellationToken);
        var current = await edge.GetConnectionForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Connection {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        EdgeRules.EnsureSettings(current.Value.Protocol, settings);
        var updated = current.Value with { Name = name, Settings = settings, IsActive = command.IsActive };
        if (updated.SameAs(current.Value))
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.EdgeConnectionUpdated, "edge_node", updated.EdgeNodeId, new { Before = current.Value, After = updated });
        var version = await edge.UpdateConnectionAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class CreateEquipmentTagHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateEquipmentTagCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateEquipmentTagCommand command, CancellationToken cancellationToken)
    {
        var address = command.Address.Trim();
        new Validator()
            .Require(TagRoles.Recorded.Contains(command.Role), "Role",
                TagRoles.All.Contains(command.Role) ? "Alarms, warnings and process values aren't recorded yet." : "Not a tag role.")
            .Require(TagRoles.CountKind(command.Role) is not null || command.CounterRollover is null, "CounterRollover", "Only a counter rolls over.")
            .ThrowIfInvalid();
        EdgeRules.EnsureTagNumbers(command.ScaleFactor, command.CounterRollover);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var connectionSnapshot = await edge.GetConnectionAsync(command.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"Connection {command.ConnectionId} not found.");
        var node = await EdgeRules.LockNodeAsync(edge, connectionSnapshot.EdgeNodeId, null, cancellationToken);
        var connection = (await edge.GetConnectionForUpdateAsync(connectionSnapshot.Id, cancellationToken))?.Value
            ?? throw new NotFoundException($"Connection {command.ConnectionId} not found.");
        if (!EdgeProtocols.IsValidAddress(connection.Protocol, address))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Address"] = ["A Sparkplug B address is group/node[/device]:metric."] });
        }

        var equipment = await edge.GetTagEquipmentAsync(command.EquipmentId, cancellationToken) ?? throw new NotFoundException($"Equipment {command.EquipmentId} not found.");
        if (equipment.SiteId != node.SiteId || !equipment.TracksState)
        {
            throw new DomainRuleException($"Bind tags to equipment of {node.Code}'s site that tracks state; {equipment.Code} doesn't qualify.");
        }

        var tag = new EquipmentTagRecord(ids.NewId(), equipment.Id, command.Role, connection.Id, address, command.ScaleFactor, command.ScaleOffset, command.CounterRollover, IsActive: true);
        var envelope = events.Create(EventTypes.EquipmentTagCreated, "equipment", equipment.Id, new { After = tag });
        var version = await edge.InsertTagAsync(tag, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(tag.Id, version);
    }
}

internal sealed class UpdateEquipmentTagHandler(IUnitOfWork unitOfWork, IEdgeRepository edge, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateEquipmentTagCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateEquipmentTagCommand command, CancellationToken cancellationToken)
    {
        var address = command.Address.Trim();
        EdgeRules.EnsureTagNumbers(command.ScaleFactor, command.CounterRollover);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var snapshot = await edge.GetTagAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Tag {command.Id} not found.");
        var connectionSnapshot = await edge.GetConnectionAsync(snapshot.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"Connection {snapshot.ConnectionId} not found.");
        await EdgeRules.LockNodeAsync(edge, connectionSnapshot.EdgeNodeId, null, cancellationToken);
        _ = await edge.GetConnectionForUpdateAsync(snapshot.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"Connection {snapshot.ConnectionId} not found.");
        var current = await edge.GetTagForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Tag {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("The tag was changed by someone else. Reload and try again.");
        }

        if (!EdgeProtocols.IsValidAddress(connectionSnapshot.Protocol, address))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Address"] = ["A Sparkplug B address is group/node[/device]:metric."] });
        }

        if (TagRoles.CountKind(current.Value.Role) is null && command.CounterRollover is not null)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["CounterRollover"] = ["Only a counter rolls over."] });
        }

        var updated = current.Value with
        {
            Address = address,
            ScaleFactor = command.ScaleFactor,
            ScaleOffset = command.ScaleOffset,
            CounterRollover = command.CounterRollover,
            IsActive = command.IsActive,
        };
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.EquipmentTagUpdated, "equipment", updated.EquipmentId, new { Before = current.Value, After = updated });
        var version = await edge.UpdateTagAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
