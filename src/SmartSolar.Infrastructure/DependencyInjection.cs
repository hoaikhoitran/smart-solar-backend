using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Messaging;
using SmartSolar.Infrastructure.Messaging.RabbitMQ;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Seed;
using SmartSolar.Infrastructure.Security;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Common.Messaging;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;

namespace SmartSolar.Infrastructure;

public static class DependencyInjection
{
    private const string DefaultConnectionName = "DefaultConnection";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        GuardDeliveryConfiguration(configuration);

        AddPersistence(services, configuration);
        AddEmail(services, configuration);
        AddMessaging(services, configuration);

        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddScoped<SystemRoleSeeder>();
        services.AddSingleton<IPasswordHashingService, PasswordHashingService>();

        return services;
    }

    private static void AddPersistence(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DefaultConnectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{DefaultConnectionName}' is not configured. " +
                "Set the 'ConnectionStrings__DefaultConnection' environment variable.");
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null)));
    }

    /// <summary>
    /// Messaging without a mailer would let the consumer acknowledge and discard
    /// verification events, losing them silently. Startup fails instead.
    /// <para>
    /// The check is deliberately one-directional. The reverse combination
    /// (mailer on, messaging off) is allowed: nothing is queued, so no message
    /// is destroyed, and every dropped event is logged at the publish site.
    /// </para>
    /// Only configuration keys are named; no values are read into the message.
    /// </summary>
    private static void GuardDeliveryConfiguration(IConfiguration configuration)
    {
        var messagingEnabled = configuration.GetValue<bool>(
            $"{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Enabled)}");
        var emailEnabled = configuration.GetValue<bool>(
            $"{EmailOptions.SectionName}:{nameof(EmailOptions.Enabled)}");

        if (messagingEnabled && !emailEnabled)
        {
            throw new InvalidOperationException(
                $"'{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Enabled)}' is true while "
                + $"'{EmailOptions.SectionName}:{nameof(EmailOptions.Enabled)}' is false. "
                + "Verification emails would be consumed and discarded. Enable "
                + $"'{EmailOptions.SectionName}:{nameof(EmailOptions.Enabled)}' and configure the "
                + $"'{EmailOptions.SectionName}:Smtp' settings, or set "
                + $"'{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Enabled)}' to false.");
        }
    }

    private static void AddEmail(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var emailSection = configuration.GetSection(EmailOptions.SectionName);

        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        services.AddOptions<EmailOptions>()
            .Bind(emailSection)
            .ValidateOnStart();

        var verificationOptions = new EmailVerificationOptions();
        configuration.GetSection(EmailVerificationOptions.SectionName).Bind(verificationOptions);
        services.TryAddSingleton(verificationOptions);

        var passwordResetOptions = new PasswordResetOptions();
        configuration.GetSection(PasswordResetOptions.SectionName).Bind(passwordResetOptions);
        services.TryAddSingleton(passwordResetOptions);

        services.AddSingleton<VerificationEmailService>();
        services.AddSingleton<PasswordResetEmailService>();

        if (emailSection.GetValue<bool>(nameof(EmailOptions.Enabled)))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
            return;
        }

        // Nothing is delivered, and each attempt logs a warning.
        services.AddSingleton<IEmailSender, DisabledEmailSender>();
    }

    private static void AddMessaging(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var rabbitMqSection = configuration.GetSection(RabbitMqOptions.SectionName);

        if (!rabbitMqSection.GetValue<bool>(nameof(RabbitMqOptions.Enabled)))
        {
            // No broker connection is made; events are dropped rather than queued.
            services.AddSingleton<IIntegrationEventPublisher, NullIntegrationEventPublisher>();
            return;
        }

        services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();

        services.AddOptions<RabbitMqOptions>()
            .Bind(rabbitMqSection)
            .ValidateOnStart();

        services.AddOptions<MassTransitHostOptions>()
            .Configure(options =>
            {
                options.WaitUntilStarted = true;
                options.StartTimeout = TimeSpan.FromSeconds(30);
                options.StopTimeout = TimeSpan.FromSeconds(30);
            });

        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<EmailVerificationRequestedConsumer>();
            bus.AddConsumer<PasswordResetRequestedConsumer>();

            bus.UsingRabbitMq((context, transport) =>
            {
                var options = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

                transport.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });

                // One durable endpoint for verification email delivery, with
                // bounded retries; exhausted messages go to the error queue.
                transport.ReceiveEndpoint(
                    EmailVerificationEndpoint.QueueName,
                    endpoint => EmailVerificationEndpoint.Configure(endpoint, context));

                transport.ReceiveEndpoint(
                    PasswordResetEndpoint.QueueName,
                    endpoint => PasswordResetEndpoint.Configure(endpoint, context));
            });
        });

        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();
    }
}
