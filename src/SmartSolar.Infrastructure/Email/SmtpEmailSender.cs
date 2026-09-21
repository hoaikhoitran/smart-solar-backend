using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using SmartSolar.Modules.Common.Email;

namespace SmartSolar.Infrastructure.Email;

/// <summary>
/// MailKit SMTP delivery. Credentials, recipients and message bodies are never
/// logged; the body carries the verification URL.
/// </summary>
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var smtp = _options.Smtp;

        if (smtp.AllowInsecureTransport)
        {
            _logger.LogWarning(
                "SMTP is configured with AllowInsecureTransport; the session may be unencrypted. "
                + "Use this only for local mail catchers.");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody
        }.ToMessageBody();

        if (SmtpClientSettings.DescribeRevocationRisk(smtp) is { } revocationWarning)
        {
            _logger.LogWarning("{Warning}", revocationWarning);
        }

        // Construction and configuration are inseparable: no validation callback
        // is installed, so invalid certificates still fail the connection.
        using var client = SmtpClientSettings.CreateClient(smtp);

        try
        {
            await client.ConnectAsync(
                smtp.Host,
                smtp.Port,
                SmtpSecurity.Resolve(smtp.UseSsl, smtp.Port, smtp.AllowInsecureTransport),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(smtp.Username))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true, CancellationToken.None);
            }
        }

        _logger.LogInformation("Verification email dispatched via SMTP host {SmtpHost}.", smtp.Host);
    }
}
