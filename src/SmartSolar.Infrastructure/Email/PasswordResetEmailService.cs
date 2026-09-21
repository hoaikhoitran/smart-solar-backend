using System.Net;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;

namespace SmartSolar.Infrastructure.Email;

/// <summary>
/// Composes the password reset email and hands it to the sender. Failures are
/// deliberately not caught: the consumer must fail so the broker can retry.
/// </summary>
public sealed class PasswordResetEmailService
{
    private const string ResetSubject = "Reset your Smart Solar password";

    private readonly IEmailSender _emailSender;
    private readonly PasswordResetOptions _resetOptions;
    private readonly ILogger<PasswordResetEmailService> _logger;

    public PasswordResetEmailService(
        IEmailSender emailSender,
        PasswordResetOptions resetOptions,
        ILogger<PasswordResetEmailService> logger)
    {
        _emailSender = emailSender;
        _resetOptions = resetOptions;
        _logger = logger;
    }

    public async Task SendPasswordResetEmailAsync(
        PasswordResetRequestedEvent message,
        CancellationToken cancellationToken)
    {
        var expiry = VerificationEmailService.DescribeLifetime(_resetOptions.LifetimeMinutes);

        await _emailSender.SendAsync(
            new EmailMessage(
                message.Email,
                ResetSubject,
                BuildHtmlBody(message.FullName, message.ResetUrl, expiry),
                BuildTextBody(message.FullName, message.ResetUrl, expiry)),
            cancellationToken);

        // The URL carries the raw token, so only the user id is logged.
        _logger.LogInformation("Password reset email sent for user {UserId}.", message.UserId);
    }

    private static string BuildHtmlBody(string fullName, string resetUrl, string expiry)
    {
        var name = WebUtility.HtmlEncode(fullName);
        var href = WebUtility.HtmlEncode(resetUrl);

        return $"""
                <html>
                  <body>
                    <p>Hello {name},</p>
                    <p>We received a request to reset your Smart Solar password.</p>
                    <p><a href="{href}">Choose a new password</a></p>
                    <p>Or paste this link into your browser:<br />{href}</p>
                    <p>This link expires in {expiry}.</p>
                    <p>If you did not request this, you can safely ignore this email; your password stays unchanged.</p>
                    <p>— Smart Solar</p>
                  </body>
                </html>
                """;
    }

    private static string BuildTextBody(string fullName, string resetUrl, string expiry)
        => $"""
            Hello {fullName},

            We received a request to reset your Smart Solar password. Open this link
            to choose a new one:

            {resetUrl}

            This link expires in {expiry}.

            If you did not request this, you can safely ignore this email; your
            password stays unchanged.

            — Smart Solar
            """;
}
