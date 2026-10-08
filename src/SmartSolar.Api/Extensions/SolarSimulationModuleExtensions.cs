using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Modules.SolarSimulation.Options;

namespace SmartSolar.Api.Extensions;

public static class SolarSimulationModuleExtensions
{
    /// <summary>
    /// Registers simulation handlers and the "SolarSimulation" options. Options are validated at
    /// startup; every value is a configurable assumption or computational limit (installation in mm).
    /// Validators live in the Modules assembly already scanned by AddIdentityModule.
    /// </summary>
    public static IServiceCollection AddSolarSimulationModule(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new SolarSimulationOptions();
        configuration.GetSection(SolarSimulationOptions.SectionName).Bind(options);
        Validate(options);

        services.AddSingleton(options);
        services.AddSingleton<SimulationSingleFlight>();
        services.AddScoped<CreateSimulationHandler>();
        services.AddScoped<GetSimulationHandler>();
        services.AddScoped<ListSimulationsHandler>();

        return services;
    }

    private static void Validate(SolarSimulationOptions o)
    {
        var errors = new List<string>();
        var limits = o.Limits;
        var d = o.PreliminaryDefaults;

        void Spacing(string name, decimal? value, decimal max)
        {
            if (value is { } v && (v < 0 || v > max)) errors.Add($"SolarSimulation:PreliminaryDefaults:{name} must be between 0 and {max} mm.");
        }

        if (limits.MaxSurfaceSideM <= 0 || limits.MaxPlacements <= 0 || limits.MaxObstacles < 0 || limits.MaxObstacleNameLength <= 0
            || limits.MaxObstacleHeightM < 0 || limits.MaxSpacingMm <= 0 || limits.MaxRackClearanceMm < 0
            || limits.MaxLayoutWorkUnits < 100_000 || limits.MaxLayoutWorkUnits > 1_000_000_000
            || limits.MinSystemLossPercent < 0 || limits.MaxSystemLossPercent < limits.MinSystemLossPercent || limits.MaxSystemLossPercent >= 100)
        {
            errors.Add("SolarSimulation:Limits contains an out-of-range value.");
        }

        Spacing(nameof(d.PanelGapMm), d.PanelGapMm, limits.MaxSpacingMm);
        Spacing(nameof(d.FlushRowGapMm), d.FlushRowGapMm, limits.MaxSpacingMm);
        Spacing(nameof(d.RackRowGapFallbackMm), d.RackRowGapFallbackMm, limits.MaxSpacingMm);
        Spacing(nameof(d.EdgeSetbackMm), d.EdgeSetbackMm, limits.MaxSpacingMm);
        Spacing(nameof(d.ObstacleClearanceMm), d.ObstacleClearanceMm, limits.MaxSpacingMm);
        Spacing(nameof(d.RackLowEdgeClearanceMm), d.RackLowEdgeClearanceMm, limits.MaxRackClearanceMm);
        if (d.ModuleThicknessMm is { } t && (t <= 0 || t > 200)) errors.Add("SolarSimulation:PreliminaryDefaults:ModuleThicknessMm must be greater than 0 and at most 200 mm.");

        if (o.DefaultSystemLossPercent < limits.MinSystemLossPercent || o.DefaultSystemLossPercent > limits.MaxSystemLossPercent)
            errors.Add("SolarSimulation:DefaultSystemLossPercent must lie within the system loss limits.");
        if (o.LayoutOffsetSamples is < 1 or > 200) errors.Add("SolarSimulation:LayoutOffsetSamples must be between 1 and 200.");
        if (o.ProviderBudgetSeconds is < 1 or > 120) errors.Add("SolarSimulation:ProviderBudgetSeconds must be between 1 and 120.");
        if (o.RackSupportHeightWarningMm <= 0) errors.Add("SolarSimulation:RackSupportHeightWarningMm must be positive.");
        if (o.DeclaredAreaMismatchRatio < 0) errors.Add("SolarSimulation:DeclaredAreaMismatchRatio must not be negative.");
        if (o.Shading.StartSolarHour < 0 || o.Shading.EndSolarHour > 24 || o.Shading.StartSolarHour > o.Shading.EndSolarHour
            || o.Shading.StepMinutes <= 0 || o.Shading.DeclinationDegree is < 0 or > 23.5)
            errors.Add("SolarSimulation:Shading contains an out-of-range value.");

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
    }
}
