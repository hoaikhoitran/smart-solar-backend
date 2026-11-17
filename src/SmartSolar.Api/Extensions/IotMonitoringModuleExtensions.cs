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

/// <summary>
/// IoT policies: reads and writes per user; ingestion per device id (and source IP), sized for one request every 10 s plus jitter, AND a
/// per-source-IP ceiling across all device ids (<see cref="IotTelemetryIngestPerIpSection"/>), so rotating the device id in the URL cannot
/// bypass the limit (audit RL-01). The ceiling is a global limiter that only applies to the ingest endpoint.
/// </summary>
public static partial class RateLimitingExtensions
{
    public const string IotReadPolicy = "iot-read";
    public const string IotWritePolicy = "iot-write";
    public const string IotTelemetryIngestPolicy = "iot-telemetry-ingest";

    /// <summary>Per source IP, all device ids together. Default 60/min: five devices behind one NAT at their 12/min ceiling. Configurable.</summary>
    public const string IotTelemetryIngestPerIpSection = "RateLimiting:IotTelemetryIngestPerIp";
    public const int IotTelemetryIngestPerIpDefault = 60;

    public static readonly IReadOnlyList<RateLimitPolicyDefinition> IotMonitoringPolicies =
    [
        new(IotReadPolicy, "RateLimiting:IotRead", 60, 1),
        new(IotWritePolicy, "RateLimiting:IotWrite", 20, 1),
        // 6 readings per minute at the agreed cycle; the margin absorbs retries and clock drift without opening a flood.
        new(IotTelemetryIngestPolicy, "RateLimiting:IotTelemetryIngest", 12, 1, RouteValuePartition: "deviceId"),
    ];

    public static IServiceCollection AddIotMonitoringRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(IotTelemetryIngestPerIpSection);
        var limit = section.GetValue<int?>("PermitLimit") ?? IotTelemetryIngestPerIpDefault;
        var window = section.GetValue<int?>("WindowMinutes") ?? 1;
        if (limit < 1 || limit > 100_000 || window < 1 || window > 1440)
            throw new InvalidOperationException($"'{IotTelemetryIngestPerIpSection}' must have PermitLimit 1-100000 and WindowMinutes 1-1440.");

        services.AddRateLimiter(options =>
            // Runs in addition to the endpoint policy. Only the ingest endpoint is partitioned; every other request gets no limiter here.
            options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var policy = context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName;
                if (policy != IotTelemetryIngestPolicy) return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("none");
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter($"{IotTelemetryIngestPerIpSection}|{ip}",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit, Window = TimeSpan.FromMinutes(window), QueueLimit = 0, AutoReplenishment = true,
                    });
            }));
        return AddFixedWindowPolicies(services, configuration, IotMonitoringPolicies);
    }
}
