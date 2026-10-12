using System.Net;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;

namespace SmartSolar.Infrastructure.Email;

/// <summary>
/// Composes the "set up your account" email for admin-provisioned accounts. Failures propagate so
/// the broker can retry, like the other identity emails.
/// </summary>
public sealed class AccountSetupEmailService(IEmailSender emailSender, AccountSetupOptions options, ILogger<AccountSetupEmailService> logger)
{
    private const string Subject = "Set up your Smart Solar account";

    public async Task SendAccountSetupEmailAsync(AccountSetupRequestedEvent message, CancellationToken cancellationToken)
    {
        var expiry = VerificationEmailService.DescribeLifetime(options.LifetimeMinutes);
        var name = WebUtility.HtmlEncode(message.FullName);
        var href = WebUtility.HtmlEncode(message.SetupUrl);

        await emailSender.SendAsync(
            new EmailMessage(
                message.Email,
                Subject,
                $"""
                <html>
                  <body>
                    <p>Hello {name},</p>
                    <p>An administrator created a Smart Solar account for you.</p>
                    <p><a href="{href}">Confirm your email and choose a password</a></p>
                    <p>Or paste this link into your browser:<br />{href}</p>
                    <p>This link can be used once and expires in {expiry}.</p>
                    <p>— Smart Solar</p>
                  </body>
                </html>
                """,
                $"""
                Hello {message.FullName},

                An administrator created a Smart Solar account for you. Open this link to
                confirm your email and choose a password:

                {message.SetupUrl}

                This link can be used once and expires in {expiry}.

                — Smart Solar
                """),
            cancellationToken);

        // The URL carries the raw token, so only the user id is logged.
        logger.LogInformation("Account setup email sent for user {UserId}.", message.UserId);
    }
}

/// <summary>Email delivery needs both the broker and SMTP (see <see cref="EmailDeliveryReadiness"/>).</summary>
public sealed class ConfiguredEmailDeliveryStatus(bool canDeliver) : IEmailDeliveryStatus
{
    public bool CanDeliver { get; } = canDeliver;
}
