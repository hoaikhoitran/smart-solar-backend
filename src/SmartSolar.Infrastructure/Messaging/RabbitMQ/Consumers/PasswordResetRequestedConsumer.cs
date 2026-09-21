using MassTransit;
using MimeKit;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>
/// Delivers the password reset email. Exceptions are allowed to escape so
/// MassTransit retries the message and, once retries are exhausted, moves it
/// to the error queue.
/// </summary>
public sealed class PasswordResetRequestedConsumer
    : IConsumer<PasswordResetRequestedEvent>
{
    private readonly PasswordResetEmailService _passwordResetEmailService;

    public PasswordResetRequestedConsumer(PasswordResetEmailService passwordResetEmailService)
    {
        _passwordResetEmailService = passwordResetEmailService;
    }

    public Task Consume(ConsumeContext<PasswordResetRequestedEvent> context)
        => _passwordResetEmailService.SendPasswordResetEmailAsync(
            context.Message,
            context.CancellationToken);
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
