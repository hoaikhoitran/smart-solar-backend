using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
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
