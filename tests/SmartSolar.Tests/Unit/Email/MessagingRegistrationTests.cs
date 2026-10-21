using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Infrastructure;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Common.Messaging;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// Which publisher, sender and consumers <c>AddInfrastructure</c> registers for each supported RabbitMQ / email combination.
/// Registration-level checks: they assert the service graph, not a running bus (queue names and retries are covered by the
/// endpoint-definition tests).
/// </summary>
public class MessagingRegistrationTests
{
    private static IServiceCollection Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused" })
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services;
    }

    private static Dictionary<string, string?> Settings(bool messaging, bool email) => new()
    {
        ["RabbitMq:Enabled"] = messaging ? "true" : "false",
        ["RabbitMq:Host"] = "localhost",
        ["RabbitMq:Port"] = "5672",
        ["RabbitMq:VirtualHost"] = "/",
        ["RabbitMq:Username"] = "guest",
        ["RabbitMq:Password"] = "guest",
        ["Email:Enabled"] = email ? "true" : "false",
        ["Email:Smtp:Host"] = email ? "smtp.example.com" : "",
        ["Email:Smtp:Port"] = "587",
        ["Email:Smtp:FromAddress"] = email ? "no-reply@example.com" : "",
    };

    private static string? Implementation<T>(IServiceCollection services) => Assert.Single(services.Where(d => d.ServiceType == typeof(T))).ImplementationType?.Name;

    private static bool Registers<T>(IServiceCollection services) => services.Any(d => d.ImplementationType == typeof(T) || d.ServiceType == typeof(T));

    [Fact]
    public void Both_disabled_starts_for_local_development_without_a_bus()
    {
        var services = Build(Settings(messaging: false, email: false));

        Assert.Equal("NullIntegrationEventPublisher", Implementation<IIntegrationEventPublisher>(services));
        Assert.Equal("DisabledEmailSender", Implementation<IEmailSender>(services));
        Assert.False(Registers<IBusControl>(services));
        Assert.False(Registers<EmailVerificationRequestedConsumer>(services));
    }

    [Fact]
    public void Both_enabled_wires_one_bus_both_consumers_and_the_smtp_sender()
    {
        var services = Build(Settings(messaging: true, email: true));

        Assert.Equal("MassTransitIntegrationEventPublisher", Implementation<IIntegrationEventPublisher>(services));
        Assert.Equal("SmtpEmailSender", Implementation<IEmailSender>(services));
        Assert.Single(services.Where(d => d.ServiceType == typeof(IBusControl)));
        Assert.True(Registers<EmailVerificationRequestedConsumer>(services));
        Assert.True(Registers<PasswordResetRequestedConsumer>(services));
    }

    [Fact]
    public void Email_without_messaging_still_starts_and_sends_directly()
    {
        // Allowed on purpose: nothing is queued, so no message can be silently destroyed (the null publisher logs every event).
        var services = Build(Settings(messaging: false, email: true));

        Assert.Equal("NullIntegrationEventPublisher", Implementation<IIntegrationEventPublisher>(services));
        Assert.Equal("SmtpEmailSender", Implementation<IEmailSender>(services));
        Assert.False(Registers<EmailVerificationRequestedConsumer>(services));
    }

    [Fact]
    public void Messaging_without_email_fails_startup_naming_the_keys_but_no_secret_values()
    {
        var settings = Settings(messaging: true, email: false);
        settings["RabbitMq:Password"] = "rabbit-secret-value";
        settings["Email:Smtp:Password"] = "smtp-secret-value";
        settings["RabbitMq:Username"] = "rabbit-user";
        settings["RabbitMq:Host"] = "rabbit-host-value";
        settings["Email:Smtp:Host"] = "smtp-host-value";

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("RabbitMq:Enabled", ex.Message);
        Assert.Contains("Email:Enabled", ex.Message);
        foreach (var secret in new[] { "rabbit-secret-value", "smtp-secret-value", "rabbit-user", "rabbit-host-value", "smtp-host-value" })
            Assert.DoesNotContain(secret, ex.Message);
    }
}
