using Pulaski.Application.Integrations;

namespace Pulaski.Api.Integrations;

/// <summary>
/// The ERP feed's settings (D-061), from <c>Integrations:Erp</c>: whether it runs, the receiver's URL and signing secret, and which
/// event types it takes (prefixes, inventory movements by default). The secret stays in configuration, never in the database.
/// </summary>
public sealed class ErpDeliveryOptions
{
    public const string Section = "Integrations:Erp";
    public const string SinkName = "webhook:erp";

    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? Secret { get; set; }
    public const string DefaultEventTypes = "pulaski.lot.";

    /// <summary>Type prefixes to send; empty means <see cref="DefaultEventTypes"/>. Not defaulted here: binding appends to an array.</summary>
    public string[] EventTypes { get; set; } = [];

    /// <summary>For a feed turned on the first time: start after the events already logged (true) or send them all (false).</summary>
    public bool StartFromNow { get; set; } = true;
    public int MaxAttempts { get; set; } = 12;
    public int TimeoutSeconds { get; set; } = 15;
    public int PollSeconds { get; set; } = 5;

    public static ErpDeliveryOptions From(IConfiguration configuration)
    {
        var options = configuration.GetSection(Section).Get<ErpDeliveryOptions>() ?? new ErpDeliveryOptions();
        if (options.EventTypes.Length == 0)
        {
            options.EventTypes = [DefaultEventTypes];
        }

        return options;
    }
}

internal sealed class DeliveryTargets(IConfiguration configuration) : IDeliveryTargets
{
    public DeliveryTarget Erp
    {
        get
        {
            var options = ErpDeliveryOptions.From(configuration);
            var host = Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
            return new DeliveryTarget(ErpDeliveryOptions.SinkName, options.Enabled && host is not null, host, options.EventTypes);
        }
    }
}
