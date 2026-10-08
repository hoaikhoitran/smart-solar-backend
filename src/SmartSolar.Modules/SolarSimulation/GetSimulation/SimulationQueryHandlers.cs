using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.GetSimulation;

public enum SimulationQueryOutcome
{
    Found,
    PreSurveyNotFound,
    Forbidden,
    SimulationNotFound
}

public sealed record GetSimulationResult(SimulationQueryOutcome Outcome, SimulationDetail? Simulation);

public sealed record ListSimulationsResult(SimulationQueryOutcome Outcome, IReadOnlyList<SimulationListItem>? Simulations);

/// <summary>Reads stored snapshots only; never calls PVGIS or NASA POWER. Stale snapshots stay readable.</summary>
public sealed class GetSimulationHandler
{
    private readonly ISolarSimulationUnitOfWork _unitOfWork;

    public GetSimulationHandler(ISolarSimulationUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<GetSimulationResult> HandleAsync(SimulationViewer viewer, Guid preSurveyId, Guid simulationId, CancellationToken cancellationToken)
    {
        var source = await _unitOfWork.GetSourceAsync(preSurveyId, cancellationToken);
        if (source is null) return new(SimulationQueryOutcome.PreSurveyNotFound, null);
        if (!SimulationAccess.CanRead(source, viewer)) return new(SimulationQueryOutcome.Forbidden, null);

        var simulation = await _unitOfWork.FindAsync(preSurveyId, simulationId, cancellationToken);
        return simulation is null
            ? new(SimulationQueryOutcome.SimulationNotFound, null)
            : new(SimulationQueryOutcome.Found, SimulationDetailMapper.ToDetail(simulation, source.GeometryVersion, source.SelectedSimulationId));
    }
}

public sealed class ListSimulationsHandler
{
    private readonly ISolarSimulationUnitOfWork _unitOfWork;

    public ListSimulationsHandler(ISolarSimulationUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <summary>Newest first (created_at, then id), so the order is deterministic.</summary>
    public async Task<ListSimulationsResult> HandleAsync(SimulationViewer viewer, Guid preSurveyId, CancellationToken cancellationToken)
    {
        var source = await _unitOfWork.GetSourceAsync(preSurveyId, cancellationToken);
        if (source is null) return new(SimulationQueryOutcome.PreSurveyNotFound, null);
        if (!SimulationAccess.CanRead(source, viewer)) return new(SimulationQueryOutcome.Forbidden, null);

        var rows = await _unitOfWork.ListAsync(preSurveyId, cancellationToken);
        return new(SimulationQueryOutcome.Found, rows.Select(r => new SimulationListItem(
            r.SimulationId, r.Status, r.EnergyStatus, r.ClimateStatus,
            r.PreSurveyGeometryVersion != source.GeometryVersion,
            r.SimulationId == source.SelectedSimulationId,
            r.PreSurveyGeometryVersion, r.MountingType, r.ProductId, r.ProductSku, r.ProductName,
            r.PanelCount, r.InstalledCapacityKwp, r.AnnualEnergyKwh, r.CreatedAt)).ToList());
    }
}
