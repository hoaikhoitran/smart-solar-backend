using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.PreSurvey.GetPreSurveySurface;
using SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Modules.SolarSimulation.Options;
using SmartSolar.Modules.SolarSimulation.SelectSimulation;
using SmartSolar.Modules.SolarSimulation.Contracts.Caching;
using SmartSolar.Modules.SolarSimulation.Contracts.Storage;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.SolarSimulation;

/// <summary>The real DI container resolves every new handler, validator and controller dependency.</summary>
public sealed class SolarSimulationCompositionTests
{
    [Fact]
    public void New_services_resolve_from_the_application_container()
    {
        using var factory = new AuthApiFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<CreateSimulationHandler>());
        Assert.NotNull(services.GetRequiredService<GetSimulationHandler>());
        Assert.NotNull(services.GetRequiredService<ListSimulationsHandler>());
        Assert.NotNull(services.GetRequiredService<UpdatePreSurveySurfaceHandler>());
        Assert.NotNull(services.GetRequiredService<GetPreSurveySurfaceHandler>());
        Assert.NotNull(services.GetRequiredService<ISolarSimulationUnitOfWork>());
        Assert.NotNull(services.GetRequiredService<IValidator<CreateSimulationCommand>>());
        Assert.NotNull(services.GetRequiredService<IValidator<UpdatePreSurveySurfaceCommand>>());
        Assert.NotNull(services.GetRequiredService<SelectSimulationHandler>());
        Assert.NotNull(services.GetRequiredService<IValidator<SelectSimulationCommand>>());
        Assert.IsType<SmartSolar.Infrastructure.Caching.DistributedSimulationResultCache>(services.GetRequiredService<ISimulationResultCache>());
        // Snapshots are off by default: the disabled store is registered and nothing is uploaded.
        Assert.IsType<SmartSolar.Infrastructure.Storage.DisabledSimulationSnapshotStore>(services.GetRequiredService<ISimulationSnapshotStore>());
        Assert.False(services.GetRequiredService<ISimulationSnapshotStore>().IsEnabled);
        Assert.Equal(1440, services.GetRequiredService<SolarSimulationOptions>().ResultTtlMinutes);
        Assert.Equal(20m, services.GetRequiredService<SolarSimulationOptions>().PreliminaryDefaults.PanelGapMm);
        Assert.Same(services.GetRequiredService<SimulationSingleFlight>(), factory.Services.GetRequiredService<SimulationSingleFlight>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<SimulationsController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<PreSurveySurfaceController>(services));
    }
}

public sealed class SolarSimulationStorageRegistrationTests
{
    [Fact]
    public void Default_application_registers_the_distributed_cache_backed_result_cache()
    {
        // Without the test replacement, the real container builds the result cache over the
        // in-process distributed cache (Redis disabled in tests).
        using var factory = new AuthApiFactory
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<ISimulationResultCache>();
                services.AddSingleton<ISimulationResultCache, SmartSolar.Infrastructure.Caching.DistributedSimulationResultCache>();
            },
        };

        var cache = factory.Services.GetRequiredService<ISimulationResultCache>();

        Assert.IsType<SmartSolar.Infrastructure.Caching.DistributedSimulationResultCache>(cache);
        Assert.IsType<Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache>(
            factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
    }

    [Fact]
    public void Enabling_snapshots_without_a_bucket_fails_at_startup()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["SolarSimulation:Snapshots:Enabled"] = "true" });

        var error = Assert.ThrowsAny<Exception>(() => factory.Services);
        Assert.Contains("SolarSimulation:Snapshots:Bucket", error.ToString());
    }
}
