using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Entities;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>
/// Sales survey-request detail and the technician job detail read the compact selected_simulation
/// summary (no cache involved), keeping the SelectedSimulationSummary shape.
/// </summary>
public sealed class SelectedSimulationDownstreamTests
{
    private static SelectedSimulation Summary(Guid preSurveyId, Guid productId, Guid userId) => new()
    {
        Id = Guid.NewGuid(), PreSurveyId = preSurveyId, SimulationId = Guid.NewGuid(), SelectedByUserId = userId,
        SelectedAt = DateTimeOffset.UtcNow, PreSurveyGeometryVersion = 0, AlgorithmVersion = AlgorithmVersions.Current,
        Status = SimulationStatusCodes.Completed, EnergyStatus = ComponentStatusCodes.Succeeded, MountingType = MountingTypes.Flush,
        ProductId = productId, ProductSku = "PNL-550", ProductName = "Mono 550", PanelCount = 24, InstalledCapacityKwp = 13.2m,
        AnnualEnergyKwh = 18480m, SpecificYieldKwhPerKwpYear = 1400m, PanelTiltDegree = 15m, PanelAzimuthDegree = 180m,
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-30),
    };

    private static async Task<SelectedSimulation> SelectAsync(SchedulingTestContext ctx, SeededSurveyRequest request)
    {
        var product = await ctx.Inner.SeedPanelProductAsync();
        var summary = Summary(request.PreSurvey.Id, product.Id, request.Customer.Id);
        ctx.Db.SelectedSimulations.Add(summary);
        await ctx.Db.SaveChangesAsync();
        await ctx.Db.PreSurveys.Where(p => p.Id == request.PreSurvey.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SelectedSimulationId, (Guid?)summary.SimulationId));
        ctx.Db.ChangeTracker.Clear();
        return summary;
    }

    [Fact]
    public async Task Sales_detail_shows_the_stored_summary()
    {
        await using var ctx = Create();
        var request = await ctx.SeedSurveyRequestAsync(District1);
        var summary = await SelectAsync(ctx, request);

        var detail = await ctx.Inner.UnitOfWork.GetSurveyRequestDetailAsync(request.Request.Id, CancellationToken.None);

        var s = detail!.SelectedSimulation!;
        Assert.Equal(summary.SimulationId, s.SimulationId);
        Assert.Equal(24, s.PanelCount);
        Assert.Equal(13.2m, s.InstalledCapacityKwp);
        Assert.Equal(18480m, s.AnnualEnergyKwh);
        Assert.Equal("PNL-550", s.ProductSku);
        Assert.False(s.IsStale);
    }

    [Fact]
    public async Task Technician_job_detail_shows_the_stored_summary()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(District1);
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), CancellationToken.None)).Succeeded);
        var summary = await SelectAsync(ctx, request);
        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.True((await ctx.AcceptAsync(tech.Id, appointment.Id)).Succeeded);

        var job = await ctx.Jobs().GetAsync(tech.Id, appointment.Id, CancellationToken.None);

        Assert.Equal(summary.SimulationId, job.Value!.SelectedSimulation!.SimulationId);
        Assert.Equal(24, job.Value.SelectedSimulation.PanelCount);
        Assert.Equal($"/api/pre-surveys/{request.PreSurvey.Id}/simulations/{summary.SimulationId}", job.Value.SimulationDetailPath);
    }
}
