using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using SmartSolar.Infrastructure.ExternalProviders.Vietmap;
using SmartSolar.Modules.Scheduling.Travel;
using SmartSolar.Modules.SolarSimulation.Energy;

namespace SmartSolar.Infrastructure.ExternalProviders;

/// <summary>
/// Typed HttpClients with a bounded resilience pipeline: retry (at most MaxRetryAttempts,
/// exponential backoff with jitter, only for timeouts, HTTP 408/429/5xx and transport errors)
/// around a per-attempt timeout. The overall budget for one simulation is enforced by the caller.
/// </summary>
public static class ExternalProvidersRegistration
{
    public static IServiceCollection AddSolarProviders(this IServiceCollection services, IConfiguration configuration)
    {
        AddPvgisClient(services, configuration);
        AddNasaPowerClient(services, configuration);
        AddVietmapClient(services, configuration);
        return services;
    }

    public static IHttpClientBuilder AddPvgisClient(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PvgisOptions>()
            .Bind(configuration.GetSection(PvgisOptions.SectionName))
            .Validate(o => IsHttpsUrl(o.BaseUrl) && o.BaseUrl.EndsWith('/'), "Providers:Pvgis:BaseUrl must be an absolute https URL ending with '/'.")
            .Validate(o => ValidResilience(o.AttemptTimeoutMilliseconds, o.MaxRetryAttempts, o.RetryBaseDelayMilliseconds) && o.CacheHours > 0,
                "Providers:Pvgis timeouts, retries and cache duration are out of range.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Technology) && !string.IsNullOrWhiteSpace(o.ProviderVersion),
                "Providers:Pvgis:Technology and ProviderVersion are required.")
            .ValidateOnStart();

        var builder = services.AddHttpClient<IPvEnergyEstimator, PvgisV53Client>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<PvgisOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        builder.AddResilienceHandler("pvgis", (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<PvgisOptions>>().Value;
            Configure(pipeline, options.MaxRetryAttempts, options.RetryBaseDelayMilliseconds, options.AttemptTimeoutMilliseconds);
        });

        return builder;
    }

    public static IHttpClientBuilder AddNasaPowerClient(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NasaPowerOptions>()
            .Bind(configuration.GetSection(NasaPowerOptions.SectionName))
            .Validate(o => IsHttpsUrl(o.BaseUrl), "Providers:NasaPower:BaseUrl must be an absolute https URL.")
            .Validate(o => o.StartYear >= 1981 && o.EndYear >= o.StartYear && o.EndYear <= 2100,
                "Providers:NasaPower:StartYear/EndYear are out of range.")
            .Validate(o => ValidResilience(o.AttemptTimeoutMilliseconds, o.MaxRetryAttempts, o.RetryBaseDelayMilliseconds) && o.CacheHours > 0,
                "Providers:NasaPower timeouts, retries and cache duration are out of range.")
            .ValidateOnStart();

        var builder = services.AddHttpClient<IClimateContextProvider, NasaPowerClimatologyClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<NasaPowerOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        builder.AddResilienceHandler("nasa-power", (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<NasaPowerOptions>>().Value;
            Configure(pipeline, options.MaxRetryAttempts, options.RetryBaseDelayMilliseconds, options.AttemptTimeoutMilliseconds);
        });

        return builder;
    }

    /// <summary>
    /// Vietmap Route v4 typed client. The default HttpClient logging handlers are removed because
    /// the request URL carries the API key. Retries cover only timeouts and transport/5xx errors;
    /// quota and rate-limit responses are never retried (that would burn quota).
    /// </summary>
    public static IHttpClientBuilder AddVietmapClient(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VietmapOptions>()
            .Bind(configuration.GetSection(VietmapOptions.SectionName))
            .PostConfigure(o =>
            {
                o.ApiKey = VietmapOptions.Clean(o.ApiKey);
                o.BaseUrl = VietmapOptions.Clean(o.BaseUrl) ?? "";
                o.Vehicle = VietmapOptions.Clean(o.Vehicle) ?? "";
            })
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ApiKey), "Providers:Vietmap:ApiKey is required when Vietmap is enabled.")
            .Validate(o => !o.Enabled || IsHttpsUrl(o.BaseUrl), "Providers:Vietmap:BaseUrl must be an absolute https URL.")
            .Validate(o => string.Equals(o.Vehicle, VietmapOptions.RequiredVehicle, StringComparison.OrdinalIgnoreCase),
                "Providers:Vietmap:Vehicle must be 'motorcycle' (all technicians ride motorcycles in V1).")
            .Validate(o => ValidResilience(o.AttemptTimeoutMilliseconds, o.MaxRetryAttempts, o.RetryBaseDelayMilliseconds)
                && o.MaxConcurrentRequests is >= 1 and <= 32 && o.CacheMinutes is >= 1 and <= 1440 && o.DepartureBucketMinutes is >= 1 and <= 60,
                "Providers:Vietmap timeouts, retries, concurrency, cache or bucket settings are out of range.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<VietmapRequestLimiter>();

        var builder = services.AddHttpClient<ITravelTimeEstimator, VietmapTravelTimeEstimator>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<VietmapOptions>>().Value;
            var baseUrl = IsHttpsUrl(options.BaseUrl) ? options.BaseUrl.TrimEnd('/') + "/" : "https://maps.vietmap.vn/";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        builder.RemoveAllLoggers();

        builder.AddResilienceHandler("vietmap", (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<VietmapOptions>>().Value;
            if (options.MaxRetryAttempts > 0)
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    Delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMilliseconds),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException or Polly.Timeout.TimeoutRejectedException
                        || args.Outcome.Result is { } response && (int)response.StatusCode >= 500),
                });
            }

            pipeline.AddTimeout(TimeSpan.FromMilliseconds(options.AttemptTimeoutMilliseconds));
        });

        return builder;
    }

    private static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> pipeline, int retries, int baseDelayMs, int attemptTimeoutMs)
    {
        if (retries > 0)
        {
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = retries,
                Delay = TimeSpan.FromMilliseconds(baseDelayMs),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            });
        }

        pipeline.AddTimeout(TimeSpan.FromMilliseconds(attemptTimeoutMs));
    }

    private static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool ValidResilience(int attemptTimeoutMs, int retries, int baseDelayMs)
        => attemptTimeoutMs is >= 10 and <= 120_000 && retries is >= 0 and <= 5 && baseDelayMs is >= 0 and <= 30_000;
}
