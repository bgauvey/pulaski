using System.Net;
using System.Net.Mail;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Pulaski.Application.Security;

namespace Pulaski.Api.Security;

/// <summary>
/// Password reset by e-mail as configured (D-093): on only with <c>Security:PasswordReset:Enabled</c>, an SMTP server and a sender in
/// <c>Email</c>, and the UI's public https address the link is made from.
/// </summary>
internal sealed class PasswordResetSettings(IOptions<SecurityOptions> security, IOptions<EmailOptions> email) : IPasswordResetSettings
{
    public bool Enabled =>
        security.Value.PasswordReset.Enabled && email.Value.IsUsable
        && Uri.TryCreate(security.Value.PasswordReset.UiBaseUrl, UriKind.Absolute, out var ui)
        && (ui.Scheme == Uri.UriSchemeHttps || (ui.Scheme == Uri.UriSchemeHttp && ui.IsLoopback));

    public TimeSpan LinkLifetime => TimeSpan.FromMinutes(Math.Clamp(security.Value.PasswordReset.LinkMinutes, 5, 1440));

    public string UiBaseUrl => security.Value.PasswordReset.UiBaseUrl;

    public bool AdministratorsOnly => security.Value.LocalSignInForAdministratorsOnly;
}

/// <summary>E-mails waiting to go (D-093): queued in memory and sent by <see cref="MailSenderService"/>, so a request never waits on the server.</summary>
internal sealed class MailOutbox(IOptions<EmailOptions> email) : IMailOutbox
{
    public bool CanSend => email.Value.IsUsable;

    private readonly Channel<OutgoingMail> _channel = Channel.CreateBounded<OutgoingMail>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<OutgoingMail> Reader => _channel.Reader;

    public void Enqueue(string recipient, string subject, string body) => _channel.Writer.TryWrite(new OutgoingMail(recipient, subject, body));
}

internal sealed record OutgoingMail(string Recipient, string Subject, string Body);

/// <summary>Sends one e-mail; the SMTP server in production, a fake in tests.</summary>
internal interface IMailSender
{
    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken);
}

internal sealed class SmtpMailSender(IOptions<EmailOptions> options) : IMailSender
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        var o = options.Value;
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.UseTls, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (o.Username is { Length: > 0 })
        {
            client.Credentials = new NetworkCredential(o.Username, o.Password);
        }

        using var message = new MailMessage(new MailAddress(o.From, o.FromName), new MailAddress(mail.Recipient))
        {
            Subject = mail.Subject,
            Body = mail.Body,
            IsBodyHtml = false,
        };
        await client.SendMailAsync(message, cancellationToken);
    }
}

/// <summary>Sends what <see cref="MailOutbox"/> holds, one at a time; a failure is logged (by the address's domain only) and not retried.</summary>
internal sealed class MailSenderService(MailOutbox outbox, IMailSender sender, ILogger<MailSenderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var mail in outbox.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await sender.SendAsync(mail, stoppingToken);
            }
            catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or IOException)
            {
                logger.LogError(ex, "Couldn't send \"{Subject}\" to an address at {Domain}.", mail.Subject, mail.Recipient.Split('@').LastOrDefault());
            }
        }
    }
}
