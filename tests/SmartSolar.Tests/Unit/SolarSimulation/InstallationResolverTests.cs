using SmartSolar.Modules.Catalog.Installation;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Options;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public class InstallationResolverTests
{
    private static readonly InstallationRequest Empty = new(null, null, null, null, null);

    private static ProductInstallationSpec Documented(params (string Key, decimal Min)[] minimums)
        => new(1, minimums.ToDictionary(m => m.Key, m => new DocumentedRequirement(m.Min, "Manual rev 3", "4.2", null, null)), null);

    [Fact]
    public void Omitted_values_use_preliminary_defaults_in_millimetres()
    {
        var result = InstallationResolver.Resolve(Empty, null, new InstallationDefaultsOptions(), MountingTypes.Flush, shadeEstimateAvailable: false);

        Assert.Empty(result.Errors);
        var installation = result.Installation!;
        Assert.Equal(20m, installation.PanelGap.ValueMm);
        Assert.Equal(InstallationSources.PreliminaryDefault, installation.PanelGap.Source);
        Assert.Equal(300m, installation.EdgeSetback.ValueMm);
        Assert.Equal(300m, installation.ObstacleClearance.ValueMm);
        Assert.Equal(40m, installation.ModuleThickness.ValueMm);
        Assert.Equal(InstallationSources.NotApplicable, installation.RackLowEdgeClearance.Source);
        Assert.Equal(0m, installation.RackLowEdgeClearance.ValueMm);
        Assert.Equal(20m, installation.RowGap.FixedValueMm);
        Assert.Equal(InstallationSources.PreliminaryDefault, installation.RowGap.FixedSource);
    }

    [Fact]
    public void Customer_value_below_documented_minimum_is_rejected_not_clamped()
    {
        var request = Empty with { PanelGapMm = 5m };

        var result = InstallationResolver.Resolve(request, Documented((InstallationRequirementKeys.PanelGapMm, 10m)),
            new InstallationDefaultsOptions(), MountingTypes.Flush, false);

        Assert.Null(result.Installation);
        var error = Assert.Single(result.Errors);
        Assert.Equal(SimulationErrorCodes.InstallationBelowDocumentedMinimum, error.Code);
        Assert.Equal("panelGapMm", error.Parameter);
    }

    [Fact]
    public void Customer_value_meeting_documented_minimum_is_site_specified()
    {
        var request = Empty with { PanelGapMm = 12m };

        var result = InstallationResolver.Resolve(request, Documented((InstallationRequirementKeys.PanelGapMm, 10m)),
            new InstallationDefaultsOptions(), MountingTypes.Flush, false);

        Assert.Equal(12m, result.Installation!.PanelGap.ValueMm);
        Assert.Equal(InstallationSources.SiteSpecified, result.Installation.PanelGap.Source);
        Assert.Equal(10m, result.Installation.PanelGap.DocumentedMinimumMm);
        Assert.Equal("Manual rev 3", result.Installation.PanelGap.DocumentRef);
    }

    [Fact]
    public void Documented_minimum_above_the_default_wins_when_value_is_omitted()
    {
        var result = InstallationResolver.Resolve(Empty, Documented((InstallationRequirementKeys.EdgeSetbackMm, 500m)),
            new InstallationDefaultsOptions(), MountingTypes.Flush, false);

        Assert.Equal(500m, result.Installation!.EdgeSetback.ValueMm);
        Assert.Equal(InstallationSources.ManufacturerDocumentedMinimum, result.Installation.EdgeSetback.Source);
    }

    [Fact]
    public void Default_above_documented_minimum_stays_the_default()
    {
        var result = InstallationResolver.Resolve(Empty, Documented((InstallationRequirementKeys.PanelGapMm, 10m)),
            new InstallationDefaultsOptions(), MountingTypes.Flush, false);

        Assert.Equal(20m, result.Installation!.PanelGap.ValueMm);
        Assert.Equal(InstallationSources.PreliminaryDefault, result.Installation.PanelGap.Source);
        Assert.Equal(10m, result.Installation.PanelGap.DocumentedMinimumMm);
    }

    [Fact]
    public void Missing_default_without_a_value_is_reported()
    {
        var defaults = new InstallationDefaultsOptions { ObstacleClearanceMm = null };

        var result = InstallationResolver.Resolve(Empty, null, defaults, MountingTypes.Flush, false);

        var error = Assert.Single(result.Errors);
        Assert.Equal(SimulationErrorCodes.InstallationParameterRequired, error.Code);
        Assert.Equal("obstacleClearanceMm", error.Parameter);
    }

    [Fact]
    public void Rack_row_gap_uses_the_shade_estimate_when_location_is_known()
    {
        var result = InstallationResolver.Resolve(Empty, null, new InstallationDefaultsOptions(), MountingTypes.Rack, shadeEstimateAvailable: true);

        var policy = result.Installation!.RowGap;
        Assert.True(policy.UseShadeEstimate);
        Assert.Null(policy.FixedValueMm);
        Assert.Equal(200m, result.Installation.RackLowEdgeClearance.ValueMm);
    }

    [Fact]
    public void Rack_row_gap_falls_back_to_the_preliminary_value_without_location()
    {
        var result = InstallationResolver.Resolve(Empty, null, new InstallationDefaultsOptions(), MountingTypes.Rack, shadeEstimateAvailable: false);

        var policy = result.Installation!.RowGap;
        Assert.False(policy.UseShadeEstimate);
        Assert.Equal(1000m, policy.FixedValueMm);
        Assert.Equal(InstallationSources.PreliminaryDefault, policy.FixedSource);
    }

    [Fact]
    public void Documented_module_thickness_replaces_the_assumed_value()
    {
        var spec = new ProductInstallationSpec(1, new Dictionary<string, DocumentedRequirement>(),
            new DocumentedDimension(35m, "Datasheet rev 2", null, null, null));

        var result = InstallationResolver.Resolve(Empty, spec, new InstallationDefaultsOptions(), MountingTypes.Flush, false);

        Assert.Equal(35m, result.Installation!.ModuleThickness.ValueMm);
        Assert.Equal(InstallationSources.ManufacturerDocumented, result.Installation.ModuleThickness.Source);
    }
}
