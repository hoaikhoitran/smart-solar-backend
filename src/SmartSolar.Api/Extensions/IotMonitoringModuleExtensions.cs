using SmartSolar.Infrastructure.Persistence.IotMonitoring;
using SmartSolar.Modules.IotMonitoring;

namespace SmartSolar.Api.Extensions;

/// <summary>IoT energy monitoring: device registry, bindings to installation projects and telemetry ingestion. Requires the Installation Flow.</summary>
public static class IotMonitoringModuleExtensions
{
    public static IServiceCollection AddIotMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ConfigurationBinding.Bind<IotMonitoringOptions>(configuration, IotMonitoringOptions.SectionName);
        if (options.Validate() is { Count: > 0 } problems)
            throw new InvalidOperationException($"Invalid '{IotMonitoringOptions.SectionName}' settings: {string.Join(" ", problems)}");
        services.AddSingleton(options);
        services.AddScoped<IIotMonitoringUnitOfWork, IotMonitoringUnitOfWork>();
        services.AddScoped<IotDeviceHandlers>();
        services.AddScoped<IotBindingHandlers>();
        services.AddScoped<IotTelemetryHandlers>();
        return services;
    }
}

/// <summary>IoT policies: reads and writes per user; ingestion per device id (and source IP), sized for one request every 10 s plus jitter.</summary>
public static partial class RateLimitingExtensions
{
    public const string IotReadPolicy = "iot-read";
    public const string IotWritePolicy = "iot-write";
    public const string IotTelemetryIngestPolicy = "iot-telemetry-ingest";

    public static readonly IReadOnlyList<RateLimitPolicyDefinition> IotMonitoringPolicies =
    [
        new(IotReadPolicy, "RateLimiting:IotRead", 60, 1),
        new(IotWritePolicy, "RateLimiting:IotWrite", 20, 1),
        // 6 readings per minute at the agreed cycle; the margin absorbs retries and clock drift without opening a flood.
        new(IotTelemetryIngestPolicy, "RateLimiting:IotTelemetryIngest", 12, 1, RouteValuePartition: "deviceId"),
    ];

    public static IServiceCollection AddIotMonitoringRateLimiting(this IServiceCollection services, IConfiguration configuration)
        => AddFixedWindowPolicies(services, configuration, IotMonitoringPolicies);
}
