using MassTransit;
using MimeKit;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>
/// The receive endpoint for verification email delivery. The values live here
/// so they can be asserted in tests; the wiring itself only runs when a bus
/// starts, which needs a reachable broker.
/// </summary>
public static class EmailVerificationEndpoint
{
    public const string QueueName = "email-verification";

    /// <summary>Retries after the first attempt, so at most 4 deliveries.</summary>
    public const int RetryLimit = 3;

    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    public static void Configure(IReceiveEndpointConfigurator endpoint, IBusRegistrationContext context)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Interval(RetryLimit, RetryInterval);

            // A malformed recipient never becomes deliverable, so it goes
            // straight to the error queue instead of burning every retry.
            retry.Ignore<ParseException>();
        });

        endpoint.ConfigureConsumer<EmailVerificationRequestedConsumer>(context);
    }
}
