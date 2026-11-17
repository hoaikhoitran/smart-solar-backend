using MassTransit;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>
/// Delivers the verification email through <see cref="AuthLinkEmailDelivery"/> (outbox-aware: dedupe, hand-back to the outbox on SMTP
/// failure, SENT on success). Messages without an outbox id keep the old behaviour: failures escape to the MassTransit retry.
/// </summary>
public sealed class EmailVerificationRequestedConsumer(VerificationEmailService verificationEmailService, AuthLinkEmailDelivery delivery)
    : IConsumer<EmailVerificationRequestedEvent>
{
    public Task Consume(ConsumeContext<EmailVerificationRequestedEvent> context)
        => delivery.DeliverAsync(context.Message.DispatchId, context.Message.UserId, context.Message.VerificationUrl,
            token => verificationEmailService.SendVerificationEmailAsync(context.Message, token), context.CancellationToken);
}
