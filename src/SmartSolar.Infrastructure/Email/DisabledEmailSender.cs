using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Email;

namespace SmartSolar.Infrastructure.Email;

/// <summary>
/// Used when Email:Enabled is false. Nothing is delivered, and every attempt is
/// logged as a warning so a disabled mailer is never mistaken for a working one.
/// </summary>
internal sealed class DisabledEmailSender : IEmailSender
{
    private readonly ILogger<DisabledEmailSender> _logger;

    public DisabledEmailSender(ILogger<DisabledEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Email delivery is disabled (Email:Enabled=false); '{Subject}' was not sent.",
            message.Subject);

        return Task.CompletedTask;
    }
}
