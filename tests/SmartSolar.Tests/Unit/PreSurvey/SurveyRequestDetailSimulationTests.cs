using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class SurveyRequestDetailSimulationTests
{
    [Fact]
    public async Task Detail_without_a_simulation_has_no_summary()
    {
        await using var ctx = new PreSurveyTestContext();
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync();
        var request = await ctx.SeedSurveyRequestAsync(preSurvey.Id);

        var detail = await ctx.UnitOfWork.GetSurveyRequestDetailAsync(request.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail!.SelectedSimulation);
        Assert.Equal(120m, detail.TotalAreaM2);
    }

    [Fact]
    public async Task Detail_summarizes_the_selected_simulation()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var created = await ctx.CreateSimulationHandler().HandleAsync(
            new CreateSimulationCommand(user.Id, draft.Id, 1, product.Id, MountingTypes.Flush, null, null, new InstallationRequest(null, null, null, null, null), null),
            CancellationToken.None);
        var submitted = await ctx.SubmitPreSurveyHandler().HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        var detail = await ctx.UnitOfWork.GetSurveyRequestDetailAsync(submitted.SurveyRequestId!.Value, CancellationToken.None);

        var summary = detail!.SelectedSimulation!;
        Assert.Equal(created.Simulation!.SimulationId, summary.SimulationId);
        Assert.Equal(created.Simulation.Layout.PanelCount, summary.PanelCount);
        Assert.Equal(created.Simulation.Layout.InstalledCapacityKwp, summary.InstalledCapacityKwp);
        Assert.Equal(created.Simulation.Energy.AnnualEnergyKwh, summary.AnnualEnergyKwh);
        Assert.False(summary.IsStale);
        Assert.Equal("PNL-550", summary.ProductSku);
    }
}
