using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Infrastructure;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Modules.Common.Email;
using SmartSolar.Modules.Common.Messaging;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// Registration-level checks: these assert the service graph, not a running bus,
/// so they cannot catch a wrong queue name or a dropped retry policy. An
/// in-memory harness test on MassTransit 8.x could cover that gap.
/// </summary>
public class MessagingRegistrationTests
{
    private static IServiceCollection Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused"
            })
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services;
    }

    private static Dictionary<string, string?> RabbitEnabled(bool emailEnabled) => new()
    {
        ["RabbitMq:Enabled"] = "true",
        ["RabbitMq:Host"] = "localhost",
        ["RabbitMq:Port"] = "5672",
        ["RabbitMq:VirtualHost"] = "/",
        ["RabbitMq:Username"] = "guest",
        ["RabbitMq:Password"] = "guest",
        ["Email:Enabled"] = emailEnabled ? "true" : "false",
        ["Email:Smtp:Host"] = emailEnabled ? "smtp.example.com" : "",
        ["Email:Smtp:Port"] = "587",
        ["Email:Smtp:FromAddress"] = emailEnabled ? "no-reply@example.com" : ""
    };

    [Fact]
    public void Registers_the_email_consumer_when_messaging_is_enabled()
    {
        var services = Build(RabbitEnabled(emailEnabled: true));

        Assert.Contains(
            services,
            d => d.ImplementationType == typeof(EmailVerificationRequestedConsumer)
                 || d.ServiceType == typeof(EmailVerificationRequestedConsumer));
    }

    [Fact]
    public void Uses_the_masstransit_publisher_when_messaging_is_enabled()
    {
        var services = Build(RabbitEnabled(emailEnabled: true));

        var descriptor = Assert.Single(
            services.Where(d => d.ServiceType == typeof(IIntegrationEventPublisher)));
        Assert.Equal("MassTransitIntegrationEventPublisher", descriptor.ImplementationType?.Name);
    }

    [Fact]
    public void Registers_exactly_one_bus()
    {
        var services = Build(RabbitEnabled(emailEnabled: true));

        Assert.Single(services.Where(d => d.ServiceType == typeof(IBusControl)));
    }

    [Fact]
    public void Keeps_the_null_publisher_and_no_consumer_when_messaging_is_disabled()
    {
        var services = Build(new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "false" });

        var descriptor = Assert.Single(
            services.Where(d => d.ServiceType == typeof(IIntegrationEventPublisher)));
        Assert.Equal("NullIntegrationEventPublisher", descriptor.ImplementationType?.Name);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IBusControl));
        Assert.DoesNotContain(
            services,
            d => d.ImplementationType == typeof(EmailVerificationRequestedConsumer));
    }

    [Fact]
    public void Uses_the_smtp_sender_when_email_is_enabled()
    {
        var services = Build(RabbitEnabled(emailEnabled: true));

        var descriptor = Assert.Single(services.Where(d => d.ServiceType == typeof(IEmailSender)));
        Assert.Equal("SmtpEmailSender", descriptor.ImplementationType?.Name);
    }

    [Fact]
    public void Uses_the_disabled_sender_for_local_development_with_everything_off()
    {
        var services = Build(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "false",
            ["Email:Enabled"] = "false"
        });

        var descriptor = Assert.Single(services.Where(d => d.ServiceType == typeof(IEmailSender)));
        Assert.Equal("DisabledEmailSender", descriptor.ImplementationType?.Name);
    }

    // --- the three supported configuration combinations ---

    [Fact]
    public void Combination_1_both_disabled_starts_for_local_development()
    {
        var services = Build(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "false",
            ["Email:Enabled"] = "false"
        });

        var publisher = Assert.Single(
            services.Where(d => d.ServiceType == typeof(IIntegrationEventPublisher)));
        Assert.Equal("NullIntegrationEventPublisher", publisher.ImplementationType?.Name);
        Assert.DoesNotContain(
            services,
            d => d.ImplementationType == typeof(EmailVerificationRequestedConsumer));
    }

    [Fact]
    public void Combination_2_both_enabled_wires_the_real_delivery_path()
    {
        var services = Build(RabbitEnabled(emailEnabled: true));

        var publisher = Assert.Single(
            services.Where(d => d.ServiceType == typeof(IIntegrationEventPublisher)));
        Assert.Equal("MassTransitIntegrationEventPublisher", publisher.ImplementationType?.Name);

        var sender = Assert.Single(services.Where(d => d.ServiceType == typeof(IEmailSender)));
        Assert.Equal("SmtpEmailSender", sender.ImplementationType?.Name);

        Assert.Contains(
            services,
            d => d.ImplementationType == typeof(EmailVerificationRequestedConsumer)
                 || d.ServiceType == typeof(EmailVerificationRequestedConsumer));
    }

    [Fact]
    public void Combination_4_email_without_messaging_still_starts()
    {
        // Allowed on purpose: nothing is queued, so no message can be silently
        // destroyed. Every dropped event is logged by the null publisher.
        var services = Build(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "false",
            ["Email:Enabled"] = "true",
            ["Email:Smtp:Host"] = "smtp.example.com",
            ["Email:Smtp:Port"] = "587",
            ["Email:Smtp:FromAddress"] = "no-reply@example.com"
        });

        var publisher = Assert.Single(
            services.Where(d => d.ServiceType == typeof(IIntegrationEventPublisher)));
        Assert.Equal("NullIntegrationEventPublisher", publisher.ImplementationType?.Name);

        var sender = Assert.Single(services.Where(d => d.ServiceType == typeof(IEmailSender)));
        Assert.Equal("SmtpEmailSender", sender.ImplementationType?.Name);

        Assert.DoesNotContain(
            services,
            d => d.ImplementationType == typeof(EmailVerificationRequestedConsumer));
    }

    [Fact]
    public void Combination_3_messaging_without_email_fails_startup_instead_of_discarding_events()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(RabbitEnabled(emailEnabled: false)));

        Assert.Contains("RabbitMq:Enabled", ex.Message);
        Assert.Contains("Email:Enabled", ex.Message);
    }

    [Fact]
    public void Combination_3_failure_message_names_keys_but_no_secret_values()
    {
        var settings = RabbitEnabled(emailEnabled: false);
        settings["RabbitMq:Password"] = "rabbit-secret-value";
        settings["Email:Smtp:Password"] = "smtp-secret-value";
        settings["RabbitMq:Username"] = "rabbit-user";
        settings["RabbitMq:Host"] = "rabbit-host-value";
        settings["Email:Smtp:Host"] = "smtp-host-value";

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.DoesNotContain("rabbit-secret-value", ex.Message);
        Assert.DoesNotContain("smtp-secret-value", ex.Message);
        Assert.DoesNotContain("rabbit-user", ex.Message);
        Assert.DoesNotContain("rabbit-host-value", ex.Message);
        Assert.DoesNotContain("smtp-host-value", ex.Message);
    }

    [Fact]
    public void Consumer_consumes_the_verification_event()
    {
        Assert.Contains(
            typeof(EmailVerificationRequestedConsumer).GetInterfaces(),
            i => i.IsGenericType
                 && i.GetGenericTypeDefinition() == typeof(IConsumer<>)
                 && i.GenericTypeArguments[0]
                     == typeof(Modules.Identity.Events.EmailVerificationRequestedEvent));
    }
}
