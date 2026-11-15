using MassTransit;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.PasswordRecovery;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>
/// Delivers the password reset email. Exceptions are allowed to escape so
/// MassTransit retries the message and, once retries are exhausted, moves it
/// to the error queue. With an outbox id (<see cref="PasswordResetRequestedEvent.DispatchId"/>) a redelivered message whose
/// email was already sent is skipped, a message whose token was replaced by a newer request is dropped (its link is dead),
/// and the outcome is recorded (SENT, or the exception type of the failed attempt). Delivery stays at-least-once: if SMTP
/// accepts the email but recording SENT fails, a retry can send it a second time.
/// </summary>
public sealed class PasswordResetRequestedConsumer
    : IConsumer<PasswordResetRequestedEvent>
{
    private readonly PasswordResetEmailService _passwordResetEmailService;
    private readonly IPasswordResetDispatchStore _dispatches;
    private readonly TimeProvider _time;
    private readonly ILogger<PasswordResetRequestedConsumer> _logger;

    public PasswordResetRequestedConsumer(PasswordResetEmailService passwordResetEmailService, IPasswordResetDispatchStore dispatches, TimeProvider time,
        ILogger<PasswordResetRequestedConsumer> logger)
    {
        _passwordResetEmailService = passwordResetEmailService;
        _dispatches = dispatches;
        _time = time;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PasswordResetRequestedEvent> context)
    {
        var message = context.Message;
        if (message.DispatchId is not { } dispatchId)
        {
            await _passwordResetEmailService.SendPasswordResetEmailAsync(message, context.CancellationToken);
            return;
        }

        var status = await _dispatches.GetStatusAsync(dispatchId, context.CancellationToken);
        if (status is null or PasswordResetDispatchStatuses.Sent or PasswordResetDispatchStatuses.Expired)
        {
            _logger.LogInformation("Password reset email for user {UserId} skipped: dispatch is {Status}.", message.UserId, status ?? "superseded");
            return;
        }

        try
        {
            await _passwordResetEmailService.SendPasswordResetEmailAsync(message, context.CancellationToken);
        }
        catch (Exception ex)
        {
            await _dispatches.RecordSendFailureAsync(dispatchId, ex.GetType().Name, CancellationToken.None);
            throw;
        }
        await _dispatches.MarkSentAsync(dispatchId, _time.GetUtcNow(), CancellationToken.None);
    }
}

/// <summary>
/// The receive endpoint for password reset delivery, mirroring the verification
/// endpoint's queue and retry contract.
/// </summary>
public static class PasswordResetEndpoint
{
    public const string QueueName = "password-reset";

    /// <summary>Retries after the first attempt, so at most 4 deliveries.</summary>
    public const int RetryLimit = 3;

    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    public static void Configure(IReceiveEndpointConfigurator endpoint, IBusRegistrationContext context)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Interval(RetryLimit, RetryInterval);

            // A malformed recipient never becomes deliverable.
            retry.Ignore<ParseException>();
        });

        endpoint.ConfigureConsumer<PasswordResetRequestedConsumer>(context);
    }
}
