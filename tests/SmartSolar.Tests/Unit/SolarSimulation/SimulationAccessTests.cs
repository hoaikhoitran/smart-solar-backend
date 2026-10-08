using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public sealed class SimulationAccessTests
{
    private static async Task<(PreSurveyTestContext Ctx, Guid OwnerId, Guid PreSurveyId, Guid SimulationId)> SeedAsync(Guid? assignedSaleId = null, bool withRequest = true)
    {
        var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var created = await ctx.CreateSimulationHandler().HandleAsync(
            new CreateSimulationCommand(user.Id, draft.Id, 1, product.Id, MountingTypes.Flush, null, null, new InstallationRequest(null, null, null, null, null), null),
            CancellationToken.None);
        if (withRequest)
        {
            await ctx.SeedSurveyRequestAsync(draft.Id, assignedSaleId is null ? SurveyRequestStatus.Pending : SurveyRequestStatus.Assigned, assignedSaleId);
        }
        return (ctx, user.Id, draft.Id, created.Simulation!.SimulationId);
    }

    [Fact]
    public async Task Owner_can_read()
    {
        var (ctx, owner, preSurveyId, simulationId) = await SeedAsync();
        await using var _ = ctx;

        var result = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(owner, true, false), preSurveyId, simulationId, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.Found, result.Outcome);
    }

    [Fact]
    public async Task Another_customer_is_forbidden()
    {
        var (ctx, _, preSurveyId, simulationId) = await SeedAsync();
        await using var __ = ctx;

        var result = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(Guid.NewGuid(), true, false), preSurveyId, simulationId, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task Assigned_sales_can_read_and_list_but_other_sales_cannot()
    {
        await using var ctx = new PreSurveyTestContext();
        var assigned = await ctx.SeedSalesUserAsync("Assigned Sales");
        var other = await ctx.SeedSalesUserAsync("Other Sales");
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var created = await ctx.CreateSimulationHandler().HandleAsync(
            new CreateSimulationCommand(user.Id, draft.Id, 1, product.Id, MountingTypes.Flush, null, null, new InstallationRequest(null, null, null, null, null), null),
            CancellationToken.None);
        await ctx.SeedSurveyRequestAsync(draft.Id, SurveyRequestStatus.Assigned, assigned.Id);
        var simulationId = created.Simulation!.SimulationId;

        var read = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(assigned.Id, false, true), draft.Id, simulationId, CancellationToken.None);
        var list = await ctx.ListSimulationsHandler().HandleAsync(new SimulationViewer(assigned.Id, false, true), draft.Id, CancellationToken.None);
        var otherRead = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(other.Id, false, true), draft.Id, simulationId, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.Found, read.Outcome);
        Assert.Single(list.Simulations!);
        Assert.Equal(SimulationQueryOutcome.Forbidden, otherRead.Outcome);
    }

    [Fact]
    public async Task Sales_cannot_read_an_unclaimed_request()
    {
        var (ctx, _, preSurveyId, simulationId) = await SeedAsync(assignedSaleId: null);
        await using var __ = ctx;

        var result = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(Guid.NewGuid(), false, true), preSurveyId, simulationId, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task Missing_pre_survey_and_simulation_are_reported()
    {
        var (ctx, owner, preSurveyId, _) = await SeedAsync();
        await using var __ = ctx;

        var noPreSurvey = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(owner, true, false), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var noSimulation = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(owner, true, false), preSurveyId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.PreSurveyNotFound, noPreSurvey.Outcome);
        Assert.Equal(SimulationQueryOutcome.SimulationNotFound, noSimulation.Outcome);
    }
}
