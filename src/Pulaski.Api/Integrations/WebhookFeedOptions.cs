using Pulaski.Application.Integrations;
using Pulaski.Domain.Integrations;

namespace Pulaski.Api.Integrations;

/// <summary>
/// A delivery feed's settings (D-061, #73, #74): the built-in <c>Integrations:Erp</c> and <c>Integrations:Documents</c>, or a feed added on the
/// screen under <c>Integrations:Feeds:{code}</c>. Configuration keeps the secret (signing and acknowledgements) and a broker's username and
/// password, never stored in the database; everything else comes from the screen once saved (<see cref="Apply"/>).
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
    public const string FeedsSection = "Integrations:Feeds";

    public const string DefaultEventTypes = "pulaski.lot.";
    public const string DefaultDocumentEventTypes = "pulaski.lot_document.";

    /// <summary>The built-in feeds, which run on their configuration until first saved.</summary>
    public static IReadOnlyList<string> Feeds => DeliveryFeedKinds.BuiltIn;

    public string Feed { get; private set; } = Erp;
    public bool IsBuiltIn => DeliveryFeedKinds.BuiltIn.Contains(Feed);
    public string Kind { get; set; } = DeliveryFeedKinds.Webhook;
    public string Sink => DeliveryFeedKinds.Sink(Kind, Feed);
    public string? Name { get; set; }

    /// <summary>What the feed is for, in messages.</summary>
    public string Label => !string.IsNullOrWhiteSpace(Name) ? Name : Feed switch
    {
        Documents => "document system",
        Erp => "ERP",
        _ => Feed,
    };

    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? Secret { get; set; }

    /// <summary>An MQTT broker's sign-in (#74), from configuration.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>An MQTT feed's topic template (#74).</summary>
    public string? Topic { get; set; }

    /// <summary>Type prefixes to send; empty means the feed's default. Not defaulted here: binding appends to an array.</summary>
    public string[] EventTypes { get; set; } = [];

    /// <summary>
    /// Send a certificate's stored file with its <c>pulaski.lot_document.attached.v1</c> event, base64 in <c>data.file</c>. Left out, on for
    /// the document feed and off for the others; without it the event carries only the reference.
    /// </summary>
    public bool? IncludeFiles { get; set; }

    /// <summary>For a feed turned on the first time: start after the events already logged (true) or send them all (false).</summary>
    public bool StartFromNow { get; set; } = true;
    public int MaxAttempts { get; set; } = 12;
    public int TimeoutSeconds { get; set; } = 15;
    public int PollSeconds { get; set; } = 5;

    public bool SendsFiles => IncludeFiles ?? Feed == Documents;

    /// <summary>
    /// Whether it can send: on, with an address, and for a webhook a secret to sign with (a broker needs none; it takes its sign-in).
    /// </summary>
    public bool IsReady(out Uri? url) =>
        Uri.TryCreate(Url, UriKind.Absolute, out url) && Enabled && (Kind == DeliveryFeedKinds.Mqtt || !string.IsNullOrEmpty(Secret));

    /// <summary>
    /// The feed's settings saved from the screen (#73) over these, which keep the secrets; null (never saved) leaves the configuration's.
    /// </summary>
    public WebhookFeedOptions Apply(DeliveryFeedSettings? saved)
    {
        if (saved is not null)
        {
            Enabled = saved.Enabled;
            Url = saved.Url;
            EventTypes = [.. DeliveryKinds.PrefixesOf(saved.EventKinds)];
            IncludeFiles = saved.IncludeFiles;
            MaxAttempts = saved.MaxAttempts;
            TimeoutSeconds = saved.TimeoutSeconds;
            PollSeconds = saved.PollSeconds;
            StartFromNow = saved.StartFromNow;
            Kind = saved.Kind;
            Topic = saved.Topic;
            Name = saved.Name.Length > 0 ? saved.Name : Name;
        }

        return this;
    }

    /// <summary>The ERP feed's settings.</summary>
    public static WebhookFeedOptions From(IConfiguration configuration) => From(configuration, Erp);

    public static WebhookFeedOptions From(IConfiguration configuration, string feed)
    {
        var section = feed switch
        {
            Erp => Section,
            Documents => DocumentsSection,
            _ => $"{FeedsSection}:{feed}",
        };
        var options = configuration.GetSection(section).Get<WebhookFeedOptions>() ?? new WebhookFeedOptions();
        options.Feed = feed;
        if (options.EventTypes.Length == 0)
        {
            options.EventTypes = [feed == Documents ? DefaultDocumentEventTypes : DefaultEventTypes];
        }

        return options;
    }
}

internal sealed class DeliveryTargets(IConfiguration configuration) : IDeliveryTargets
{
    public DeliveryTarget Erp => Find(WebhookFeedOptions.Erp)!;

    public DeliveryTarget? Find(string feed)
    {
        if (!DeliveryFeedKinds.BuiltIn.Contains(feed) && DeliveryFeedKinds.CodeProblem(feed) is not null)
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
            IsBuiltIn = options.IsBuiltIn,
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
