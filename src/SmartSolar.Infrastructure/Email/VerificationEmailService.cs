using System.Net;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;

namespace SmartSolar.Infrastructure.Email;

/// <summary>
/// Composes the verification email and hands it to the sender. Failures are
/// deliberately not caught: the consumer must fail so the broker can retry.
/// </summary>
public sealed class VerificationEmailService
{
    private const string VerificationSubject = "Verify your Smart Solar account";

    private readonly IEmailSender _emailSender;
    private readonly EmailVerificationOptions _verificationOptions;
    private readonly ILogger<VerificationEmailService> _logger;

    public VerificationEmailService(
        IEmailSender emailSender,
        EmailVerificationOptions verificationOptions,
        ILogger<VerificationEmailService> logger)
    {
        _emailSender = emailSender;
        _verificationOptions = verificationOptions;
        _logger = logger;
    }

    public async Task SendVerificationEmailAsync(
        EmailVerificationRequestedEvent message,
        CancellationToken cancellationToken)
    {
        // The wording follows the configured lifetime so the email cannot
        // promise a validity the token does not have.
        var expiry = DescribeLifetime(_verificationOptions.LifetimeMinutes);

        await _emailSender.SendAsync(
            new EmailMessage(
                message.Email,
                VerificationSubject,
                BuildHtmlBody(message.FullName, message.VerificationUrl, expiry),
                BuildTextBody(message.FullName, message.VerificationUrl, expiry)),
            cancellationToken);

        // The URL carries the raw token, so only the user id is logged.
        _logger.LogInformation("Verification email sent for user {UserId}.", message.UserId);
    }

    internal static string DescribeLifetime(int lifetimeMinutes)
    {
        if (lifetimeMinutes % 60 != 0)
        {
            return $"{lifetimeMinutes} minutes";
        }

        var hours = lifetimeMinutes / 60;

        return hours == 1 ? "1 hour" : $"{hours} hours";
    }

    private static string BuildHtmlBody(string fullName, string verificationUrl, string expiry)
    {
        // The name comes from user input and is encoded; the URL is built by the
        // application and encoded for use in an attribute.
        var name = WebUtility.HtmlEncode(fullName);
        var href = WebUtility.HtmlEncode(verificationUrl);

        return $"""
                <html>
                  <body>
                    <p>Hello {name},</p>
                    <p>Thanks for creating a Smart Solar account. Please confirm your email address to activate it.</p>
                    <p><a href="{href}">Verify my email</a></p>
                    <p>Or paste this link into your browser:<br />{href}</p>
                    <p>This link expires in {expiry}.</p>
                    <p>If you did not create this account, you can safely ignore this email.</p>
                    <p>— Smart Solar</p>
                  </body>
                </html>
                """;
    }

    private static string BuildTextBody(string fullName, string verificationUrl, string expiry)
        => $"""
            Hello {fullName},

            Thanks for creating a Smart Solar account. Please confirm your email address
            to activate it by opening this link:

            {verificationUrl}

            This link expires in {expiry}.

            If you did not create this account, you can safely ignore this email.

            — Smart Solar
            """;
}
