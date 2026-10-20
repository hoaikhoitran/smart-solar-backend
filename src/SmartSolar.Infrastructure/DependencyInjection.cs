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
using SmartSolar.Infrastructure.Caching;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Infrastructure.ExternalProviders;

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
        AddCaching(services, configuration);

        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddScoped<ICatalogUnitOfWork, CatalogUnitOfWork>();
        services.AddScoped<IProductImageUnitOfWork, ProductImageUnitOfWork>();
        services.AddScoped<SystemRoleSeeder>();
        services.AddScoped<IPreSurveyUnitOfWork, PreSurveyUnitOfWork>();
        services.AddScoped<ISolarSimulationUnitOfWork, SolarSimulationUnitOfWork>();
        AddSimulationResultStorage(services, configuration);
        var includeInstallationAssignments = SmartSolar.Infrastructure.Features.FeatureConfiguration.Read(configuration).InstallationWorkflowEnabled;
        services.AddScoped<ISchedulingUnitOfWork>(sp => new SchedulingUnitOfWork(sp.GetRequiredService<AppDbContext>(), includeInstallationAssignments,
            sp.GetService<TimeProvider>()));
        services.AddScoped<SmartSolar.Modules.AdministrativeUnits.IAdministrativeCatalogReader, Persistence.AdministrativeUnits.AdministrativeCatalogStore>();
        services.AddScoped<IAccessTokenRevocationCheck, AccessTokenRevocationCheck>();
        services.AddScoped<IAdminUserStore>(sp => new AdminUserStore(sp.GetRequiredService<AppDbContext>(), includeInstallationAssignments));
        services.AddSolarProviders(configuration);
        services.AddSingleton<IPasswordHashingService, PasswordHashingService>();

        return services;
    }

    
    private static void AddCaching(
    IServiceCollection services,
    IConfiguration configuration)
    {
        var section = configuration.GetSection(RedisOptions.SectionName);

        var enabled = section.GetValue<bool>(
            nameof(RedisOptions.Enabled));

        // RedisCacheStore only talks to IDistributedCache, so with Redis disabled
        // it runs over the in-process cache and cache consumers still resolve.
        services.AddSingleton<ICacheStore, RedisCacheStore>();

        if (!enabled)
        {
            services.AddDistributedMemoryCache();
            return;
        }

        var connectionString = section[
            nameof(RedisOptions.ConnectionString)];

        var instanceName = section[
            nameof(RedisOptions.InstanceName)];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Redis:ConnectionString is required when Redis is enabled.");
        }

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = connectionString;
            options.InstanceName = string.IsNullOrWhiteSpace(instanceName)
                ? "SmartSolar:"
                : instanceName;
        });

        // Shared counters for the cluster-wide rate-limit policies. The connection is lazy and never
        // aborts startup: while Redis is unreachable those policies fall back to per-instance limits.
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        {
            var redis = StackExchange.Redis.ConfigurationOptions.Parse(connectionString);
            redis.AbortOnConnectFail = false;
            redis.ConnectTimeout = 2000;
            redis.SyncTimeout = 1000;
            redis.AsyncTimeout = 1000;
            return StackExchange.Redis.ConnectionMultiplexer.Connect(redis);
        });
        services.AddSingleton<SmartSolar.Modules.Common.RateLimiting.IDistributedRateLimitCounter>(sp => new RedisRateLimitCounter(
            sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
            (string.IsNullOrWhiteSpace(instanceName) ? "SmartSolar:" : instanceName) + "ratelimit:"));
    }

    /// <summary>
    /// Temporary full simulation results go to the distributed cache registered by
    /// <see cref="AddCaching"/> (Redis, or the in-process memory cache when Redis is disabled:
    /// single instance only). The optional durable snapshot store is S3 when
    /// 'SolarSimulation:Snapshots:Enabled' is true, otherwise disabled. Snapshot credentials are
    /// the default AWS chain or the optional Storage:S3 static keys; none are read into logs.
    /// </summary>
    private static void AddSimulationResultStorage(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SmartSolar.Modules.SolarSimulation.Contracts.Caching.ISimulationResultCache, DistributedSimulationResultCache>();

        var snapshots = new SmartSolar.Modules.SolarSimulation.Options.SimulationSnapshotOptions();
        configuration.GetSection("SolarSimulation:Snapshots").Bind(snapshots);
        if (!snapshots.Enabled)
        {
            services.AddSingleton<SmartSolar.Modules.SolarSimulation.Contracts.Storage.ISimulationSnapshotStore, SmartSolar.Infrastructure.Storage.DisabledSimulationSnapshotStore>();
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshots.Bucket))
        {
            throw new InvalidOperationException(
                "'SolarSimulation:Snapshots:Bucket' is required when 'SolarSimulation:Snapshots:Enabled' is true.");
        }

        var accessKeyId = configuration["Storage:S3:AccessKeyId"];
        var secretAccessKey = configuration["Storage:S3:SecretAccessKey"];
        services.AddSingleton<SmartSolar.Modules.SolarSimulation.Contracts.Storage.ISimulationSnapshotStore>(sp =>
            new SmartSolar.Infrastructure.Storage.S3SimulationSnapshotStore(
                SmartSolar.Infrastructure.Storage.S3SimulationSnapshotStore.CreateClient(snapshots, accessKeyId, secretAccessKey),
                snapshots,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SmartSolar.Infrastructure.Storage.S3SimulationSnapshotStore>>()));
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

        var accountSetupOptions = new AccountSetupOptions();
        configuration.GetSection(AccountSetupOptions.SectionName).Bind(accountSetupOptions);
        services.TryAddSingleton(accountSetupOptions);
        services.AddSingleton<AccountSetupEmailService>();

        services.TryAddSingleton<IEmailDeliveryStatus>(new ConfiguredEmailDeliveryStatus(EmailDeliveryReadiness.Describe(
            configuration.GetValue<bool>($"{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Enabled)}"),
            emailSection.GetValue<bool>(nameof(EmailOptions.Enabled))).CanDeliver));

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
            bus.AddConsumer<AccountSetupRequestedConsumer>();

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

                transport.ReceiveEndpoint(
                    AccountSetupEndpoint.QueueName,
                    endpoint => AccountSetupEndpoint.Configure(endpoint, context));
            });
        });

        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();
    }
}
