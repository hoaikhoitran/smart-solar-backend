using MassTransit;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>
/// Delivers the verification email. Exceptions are allowed to escape so
/// MassTransit retries the message and, once retries are exhausted, moves it
/// to the error queue.
/// </summary>
public sealed class EmailVerificationRequestedConsumer
    : IConsumer<EmailVerificationRequestedEvent>
{
    private readonly VerificationEmailService _verificationEmailService;

    public EmailVerificationRequestedConsumer(VerificationEmailService verificationEmailService)
    {
        _verificationEmailService = verificationEmailService;
    }

    public Task Consume(ConsumeContext<EmailVerificationRequestedEvent> context)
        => _verificationEmailService.SendVerificationEmailAsync(
            context.Message,
            context.CancellationToken);
}
