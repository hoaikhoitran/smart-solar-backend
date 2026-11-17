using MassTransit;
using MimeKit;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>Delivers the account setup email through <see cref="AuthLinkEmailDelivery"/> (outbox-aware, like password reset).</summary>
public sealed class AccountSetupRequestedConsumer(AccountSetupEmailService emailService, AuthLinkEmailDelivery delivery) : IConsumer<AccountSetupRequestedEvent>
{
    public Task Consume(ConsumeContext<AccountSetupRequestedEvent> context)
        => delivery.DeliverAsync(context.Message.DispatchId, context.Message.UserId, context.Message.SetupUrl,
            token => emailService.SendAccountSetupEmailAsync(context.Message, token), context.CancellationToken);
}

/// <summary>Receive endpoint for account setup emails, with the same retry contract as password reset.</summary>
public static class AccountSetupEndpoint
{
    public const string QueueName = "account-setup";

    public const int RetryLimit = 3;

    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    public static void Configure(IReceiveEndpointConfigurator endpoint, IBusRegistrationContext context)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Interval(RetryLimit, RetryInterval);
            retry.Ignore<ParseException>();
        });

        endpoint.ConfigureConsumer<AccountSetupRequestedConsumer>(context);
    }
}
