using MassTransit;
using MimeKit;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>Delivers the account setup email; exceptions escape so MassTransit retries.</summary>
public sealed class AccountSetupRequestedConsumer(AccountSetupEmailService emailService) : IConsumer<AccountSetupRequestedEvent>
{
    public Task Consume(ConsumeContext<AccountSetupRequestedEvent> context)
        => emailService.SendAccountSetupEmailAsync(context.Message, context.CancellationToken);
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
