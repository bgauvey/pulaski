using Pulaski.Application.Integrations;

namespace Pulaski.Api.Integrations;

/// <summary>
/// A webhook feed's settings (D-061, #78), from <c>Integrations:Erp</c> or <c>Integrations:Documents</c>: whether it runs, the receiver's URL
/// and signing secret, which event types it takes (prefixes), and whether a certificate's file goes with its event. The secret stays in
/// configuration, never in the database.
/// </summary>
public sealed class WebhookFeedOptions
{
    public const string Erp = "erp";
    public const string Documents = "documents";

    /// <summary>The ERP feed's section and sink, kept as they were before there was more than one feed.</summary>
    public const string Section = "Integrations:Erp";
    public const string SinkName = "webhook:erp";
    public const string DocumentsSection = "Integrations:Documents";
    public const string DocumentsSinkName = "webhook:documents";

    public const string DefaultEventTypes = "pulaski.lot.";
    public const string DefaultDocumentEventTypes = "pulaski.lot_document.";

    /// <summary>Feeds by key, for routes and screens.</summary>
    public static IReadOnlyList<string> Feeds { get; } = [Erp, Documents];

    public string Feed { get; private set; } = Erp;
    public string Sink => Feed == Documents ? DocumentsSinkName : SinkName;

    /// <summary>What the feed is for, in messages.</summary>
    public string Label => Feed == Documents ? "document system" : "ERP";

    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? Secret { get; set; }

    /// <summary>Type prefixes to send; empty means the feed's default. Not defaulted here: binding appends to an array.</summary>
    public string[] EventTypes { get; set; } = [];

    /// <summary>
    /// Send a certificate's stored file with its <c>pulaski.lot_document.attached.v1</c> event, base64 in <c>data.file</c>. Left out, on for
    /// the document feed and off for the ERP feed; without it the event carries only the reference.
    /// </summary>
    public bool? IncludeFiles { get; set; }

    /// <summary>For a feed turned on the first time: start after the events already logged (true) or send them all (false).</summary>
    public bool StartFromNow { get; set; } = true;
    public int MaxAttempts { get; set; } = 12;
    public int TimeoutSeconds { get; set; } = 15;
    public int PollSeconds { get; set; } = 5;

    public bool SendsFiles => IncludeFiles ?? Feed == Documents;

    /// <summary>
    /// The feed's settings saved from the screen (#73) over these, which keep the secret; null (never saved) leaves the configuration's.
    /// </summary>
    public WebhookFeedOptions Apply(Pulaski.Domain.Integrations.DeliveryFeedSettings? saved)
    {
        if (saved is not null)
        {
            Enabled = saved.Enabled;
            Url = saved.Url;
            EventTypes = [.. Pulaski.Domain.Integrations.DeliveryKinds.PrefixesOf(saved.EventKinds)];
            IncludeFiles = saved.IncludeFiles;
            MaxAttempts = saved.MaxAttempts;
            TimeoutSeconds = saved.TimeoutSeconds;
            PollSeconds = saved.PollSeconds;
            StartFromNow = saved.StartFromNow;
        }

        return this;
    }

    /// <summary>The ERP feed's settings.</summary>
    public static WebhookFeedOptions From(IConfiguration configuration) => From(configuration, Erp);

    public static WebhookFeedOptions From(IConfiguration configuration, string feed)
    {
        var documents = feed == Documents;
        var options = configuration.GetSection(documents ? DocumentsSection : Section).Get<WebhookFeedOptions>() ?? new WebhookFeedOptions();
        options.Feed = documents ? Documents : Erp;
        if (options.EventTypes.Length == 0)
        {
            options.EventTypes = [documents ? DefaultDocumentEventTypes : DefaultEventTypes];
        }

        return options;
    }
}

internal sealed class DeliveryTargets(IConfiguration configuration) : IDeliveryTargets
{
    public DeliveryTarget Erp => Find(WebhookFeedOptions.Erp)!;

    public DeliveryTarget? Find(string feed)
    {
        if (!WebhookFeedOptions.Feeds.Contains(feed))
        {
            return null;
        }

        var options = WebhookFeedOptions.From(configuration, feed);
        var host = Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
        return new DeliveryTarget(options.Sink, options.Enabled && host is not null, host, options.EventTypes)
        {
            Feed = feed,
            Label = options.Label,
            IncludesFiles = options.SendsFiles,
            Url = options.Url,
            MaxAttempts = options.MaxAttempts,
            TimeoutSeconds = options.TimeoutSeconds,
            PollSeconds = options.PollSeconds,
            StartFromNow = options.StartFromNow,
            SecretConfigured = !string.IsNullOrEmpty(options.Secret),
        };
    }
}

/// <summary>
/// Checks a receiver's signed acknowledgement (#73): <c>X-Pulaski-Signature</c> must be <c>sha256=</c> and the hex HMAC-SHA256, with the feed's
/// secret, of <c>X-Pulaski-Timestamp</c>, a full stop and the body, as Pulaski signs its own messages; the timestamp within five minutes.
/// </summary>
public static class AcknowledgementSignature
{
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static bool IsValid(string secret, string? timestamp, string? signature, string body, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > MaxSkew
            || signature is null)
        {
            return false;
        }

        var expected = System.Text.Encoding.ASCII.GetBytes(WebhookSender.Signature(secret, timestamp!, body));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, System.Text.Encoding.ASCII.GetBytes(signature));
    }
}
