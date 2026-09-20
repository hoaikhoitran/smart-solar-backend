using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartSolar.Infrastructure.Messaging;
using SmartSolar.Infrastructure.Messaging.RabbitMQ;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Common.Messaging;

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

        AddPersistence(services, configuration);
        AddMessaging(services, configuration);

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

    private static void AddMessaging(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var rabbitMqSection = configuration.GetSection(RabbitMqOptions.SectionName);

        if (!rabbitMqSection.GetValue<bool>(nameof(RabbitMqOptions.Enabled)))
        {
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
            bus.UsingRabbitMq((context, transport) =>
            {
                var options = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

                transport.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });
            });
        });

        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();
    }
}
