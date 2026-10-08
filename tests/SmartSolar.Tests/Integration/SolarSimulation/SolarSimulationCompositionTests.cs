using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.PreSurvey.GetPreSurveySurface;
using SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Modules.SolarSimulation.Options;
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
        Assert.Equal(20m, services.GetRequiredService<SolarSimulationOptions>().PreliminaryDefaults.PanelGapMm);
        Assert.Same(services.GetRequiredService<SimulationSingleFlight>(), factory.Services.GetRequiredService<SimulationSingleFlight>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<SimulationsController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<PreSurveySurfaceController>(services));
    }
}
