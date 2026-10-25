using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Pulaski.Connectors.OpcUa;

/// <summary>
/// An OPC UA session through the OPC Foundation's UA .NET Standard stack (#22). The edge's application certificate and trust lists live
/// in <see cref="OpcUaOptions.PkiRoot"/>: <c>own</c>, <c>trusted</c>, <c>issuer</c>, <c>rejected</c>. A server certificate is accepted
/// when it is in <c>trusted</c>, matches the connection's thumbprint (it is then added to <c>trusted</c>), or the connection trusts any
/// server; otherwise it is refused and kept in <c>rejected</c>, for someone to move to <c>trusted</c>.
/// </summary>
internal sealed class OpcFoundationSession(OpcUaOptions options, ILoggerFactory loggers) : IOpcUaSession
{
    private const int LostAfterKeepAlives = 3;

    private readonly EdgeTelemetry _telemetry = new(loggers);
    private ApplicationInstance? _application;
    private readonly Dictionary<string, MonitoredItem> _items = new(StringComparer.Ordinal);
    private ISession? _session;
    private Subscription? _subscription;
    private int _failedKeepAlives;

    public event Action<OpcUaValue>? ValueChanged;

    public event Action<string>? ConnectionLost;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // The endpoint first: a server certificate trusted by its thumbprint goes into the trusted store before the validator reads it.
        var chosen = await SelectEndpointAsync(cancellationToken);
        var configuration = await ConfigureAsync(cancellationToken);
        var endpoint = new ConfiguredEndpoint(null, chosen, EndpointConfiguration.Create(configuration));
        var identity = options.Username is { } user
            ? new UserIdentity(user, System.Text.Encoding.UTF8.GetBytes(options.Password ?? ""))
            : new UserIdentity();
        _session = await new DefaultSessionFactory(_telemetry).CreateAsync(
            configuration, endpoint, false, false, $"Pulaski Edge {options.ConnectionCode}", 60_000, identity, default, cancellationToken);
        _session.KeepAlive += OnKeepAlive;

        _subscription = new Subscription(_telemetry, new SubscriptionOptions
        {
            DisplayName = $"Pulaski {options.ConnectionCode}",
            PublishingInterval = (int)options.PublishingInterval.TotalMilliseconds,
            KeepAliveCount = 10,
            LifetimeCount = 100,
            PublishingEnabled = true,
            TimestampsToReturn = TimestampsToReturn.Both,
        });
        _session.AddSubscription(_subscription);
        await _subscription.CreateAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OpcUaNode>> BrowseAsync(string? root, int depth, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("Not connected.");
        var found = new List<OpcUaNode>();
        var browser = new Browser(session, new BrowserOptions
        {
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
        });
        var level = new List<NodeId> { root is null ? ObjectIds.ObjectsFolder : NodeId.Parse(root) };
        for (var d = 0; d < Math.Max(1, depth) && level.Count > 0 && found.Count < 5000; d++)
        {
            var next = new List<NodeId>();
            foreach (var node in level)
            {
                foreach (var reference in (await browser.BrowseAsync(node, cancellationToken)).ToList())
                {
                    var id = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris, false);
                    if (id.IsNull)
                    {
                        continue;
                    }

                    if (reference.NodeClass == NodeClass.Variable)
                    {
                        found.Add(new OpcUaNode(id.ToString(), null, null));
                    }

                    next.Add(id);
                }
            }

            level = next;
        }

        return found;
    }

    public async Task MonitorAsync(IReadOnlySet<string> nodeIds, CancellationToken cancellationToken)
    {
        var subscription = _subscription ?? throw new InvalidOperationException("Not connected.");
        foreach (var gone in _items.Keys.Where(k => !nodeIds.Contains(k)).ToList())
        {
            subscription.RemoveItem(_items[gone]);
            _items.Remove(gone);
        }

        foreach (var id in nodeIds.Where(id => !_items.ContainsKey(id)))
        {
            var item = new MonitoredItem(_telemetry, new MonitoredItemOptions
            {
                DisplayName = id,
                StartNodeId = NodeId.Parse(id),
                AttributeId = Attributes.Value,
                SamplingInterval = (int)options.SamplingInterval.TotalMilliseconds,
                QueueSize = 10,
                DiscardOldest = true,
            });
            item.Notification += (monitored, e) => OnNotification(id, e);
            subscription.AddItem(item);
            _items[id] = item;
        }

        await subscription.ApplyChangesAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            session.KeepAlive -= OnKeepAlive;
            try
            {
                await session.CloseAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is ServiceResultException or ObjectDisposedException)
            {
                // Closing a session the server already dropped.
            }

            session.Dispose();
        }

        if (_application is not null)
        {
            await _application.DisposeAsync();
        }

        _telemetry.Dispose();
    }

    private void OnNotification(string nodeId, MonitoredItemNotificationEventArgs e)
    {
        if (e.NotificationValue is MonitoredItemNotification { Value: { } value })
        {
            ValueChanged?.Invoke(new OpcUaValue(
                nodeId,
                Plain(value.WrappedValue.AsBoxedObject(Variant.BoxingBehavior.None)),
                StatusCode.IsGood(value.StatusCode),
                Time(value.SourceTimestamp),
                Time(value.ServerTimestamp)));
        }
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsGood(e.Status))
        {
            _failedKeepAlives = 0;
            return;
        }

        if (++_failedKeepAlives == LostAfterKeepAlives)
        {
            e.CancelKeepAlive = true;
            ConnectionLost?.Invoke($"the server stopped answering ({e.Status})");
        }
    }

    private async Task<ApplicationConfiguration> ConfigureAsync(CancellationToken cancellationToken)
    {
        _application = new ApplicationInstance(_telemetry) { ApplicationName = "Pulaski Edge", ApplicationType = ApplicationType.Client };
        var configuration = await _application
            .Build($"urn:{Environment.MachineName}:pulaski:edge", "urn:pulaski:edge")
            .AsClient()
            .AddSecurityConfiguration(
                ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates("CN=Pulaski Edge, O=Pulaski", CertificateStoreType.Directory, Path.Combine(options.PkiRoot, "own")),
                options.PkiRoot,
                Path.Combine(options.PkiRoot, "rejected"))
            .SetAutoAcceptUntrustedCertificates(options.TrustAnyServer)
            .CreateAsync(cancellationToken);
        await _application.CheckApplicationInstanceCertificatesAsync(false, null, cancellationToken);
        return configuration;
    }

    // The endpoint with the security asked for: the best policy the server offers for that mode, or the one named.
    private async Task<EndpointDescription> SelectEndpointAsync(CancellationToken cancellationToken)
    {
        var mode = options.SecurityMode switch
        {
            "none" => MessageSecurityMode.None,
            "sign" => MessageSecurityMode.Sign,
            _ => MessageSecurityMode.SignAndEncrypt,
        };
        using var discovery = await DiscoveryClient.CreateAsync(options.Endpoint, EndpointConfiguration.Create(), _telemetry, DiagnosticsMasks.None, cancellationToken);
        var offered = (await discovery.GetEndpointsAsync(default, cancellationToken)).ToList();
        var chosen = offered
            .Where(e => e.SecurityMode == mode && e.EndpointUrl?.StartsWith("opc.tcp", StringComparison.OrdinalIgnoreCase) == true)
            .Where(e => options.SecurityPolicy is null || e.SecurityPolicyUri?.EndsWith("#" + options.SecurityPolicy, StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(e => e.SecurityLevel)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{options.Endpoint} offers no {options.SecurityMode} endpoint{(options.SecurityPolicy is null ? "" : $" with {options.SecurityPolicy}")}.");

        if (mode != MessageSecurityMode.None && options.ServerThumbprint is { } wanted && chosen.ServerCertificate is { IsEmpty: false } bytes)
        {
            using var certificate = Opc.Ua.Security.Certificates.Certificate.FromRawData(bytes.ToArray());
            if (!string.Equals(certificate.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{options.Endpoint}'s certificate has thumbprint {certificate.Thumbprint}, not {wanted}; it isn't trusted.");
            }

            // A directory store keeps each certificate as certs/<thumbprint>.der.
            var certs = Directory.CreateDirectory(Path.Combine(options.PkiRoot, "trusted", "certs"));
            await File.WriteAllBytesAsync(Path.Combine(certs.FullName, certificate.Thumbprint + ".der"), certificate.RawData, cancellationToken);
        }

        // The endpoint as configured: the server may announce a host name the edge can't resolve, so keep the address given.
        chosen.EndpointUrl = new UriBuilder(chosen.EndpointUrl!) { Host = options.Endpoint.Host, Port = options.Endpoint.Port }.Uri.ToString();
        return chosen;
    }

    private static DateTimeOffset? Time(DateTimeUtc at) => at.IsNull ? null : at.ToDateTimeOffset();

    // Numbers, booleans and text as they are; anything else (arrays, structures, localized text) as text.
    private static object? Plain(object? value) => value switch
    {
        null => null,
        bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or string => value,
        _ => value.ToString(),
    };
}

/// <summary>The stack's logging and metrics, through the edge's own loggers.</summary>
internal sealed class EdgeTelemetry(ILoggerFactory loggers) : ITelemetryContext, IDisposable
{
    private readonly ActivitySource _activities = new("Pulaski.Edge.OpcUa");

    public ILoggerFactory LoggerFactory => loggers;

    public ActivitySource ActivitySource => _activities;

    public Meter CreateMeter() => new("Pulaski.Edge.OpcUa");

    public Meter CreateMeter(Assembly assembly) => new(assembly.GetName().Name ?? "Pulaski.Edge.OpcUa");

    public ActivitySource GetActivitySource(Assembly assembly) => _activities;

    public void Dispose() => _activities.Dispose();
}
