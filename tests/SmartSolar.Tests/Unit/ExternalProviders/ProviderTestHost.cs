using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Infrastructure.ExternalProviders;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.ExternalProviders;

/// <summary>Builds the production client registration with a stub primary handler and fast retries.</summary>
internal static class ProviderTestHost
{
    private static IConfiguration Config(string prefix) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{prefix}:RetryBaseDelayMilliseconds"] = "1",
            [$"{prefix}:AttemptTimeoutMilliseconds"] = "200",
        })
        .Build();

    public static IPvEnergyEstimator Pvgis(StubHttpMessageHandler stub, ICacheStore cache)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(cache);
        ExternalProvidersRegistration.AddPvgisClient(services, Config(PvgisOptions.SectionName))
            .ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider().GetRequiredService<IPvEnergyEstimator>();
    }

    public static IClimateContextProvider NasaPower(StubHttpMessageHandler stub, ICacheStore cache)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(cache);
        ExternalProvidersRegistration.AddNasaPowerClient(services, Config(NasaPowerOptions.SectionName))
            .ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider().GetRequiredService<IClimateContextProvider>();
    }
}
