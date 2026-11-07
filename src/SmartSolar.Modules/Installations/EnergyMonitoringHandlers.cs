using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Installations;

/// <summary>Expected production from the customer's selected simulation (a pre-installation estimate, never a measurement).</summary>
public sealed record SimulatedProductionView(Guid SimulationId, DateTimeOffset SelectedAt, string AlgorithmVersion, string EnergyStatus, int PanelCount,
    decimal InstalledCapacityKwp, decimal? AnnualEnergyKwh, decimal? SpecificYieldKwhPerKwpYear);

/// <summary>
/// Energy monitoring of one installation. <see cref="DataSource"/> says where numbers come from; today only <c>SIMULATED</c> exists
/// (no meter, inverter or IoT integration), so <see cref="MeasuredDataAvailable"/> is false, <see cref="Status"/> is
/// <c>NO_MEASURED_DATA</c> and no abnormal-production alert can be raised (<see cref="AlertsStatus"/> = <c>NOT_CONFIGURED</c>: there is
/// neither measured data nor an approved threshold).
/// </summary>
public sealed record EnergyMonitoringView(Guid ProjectId, string ProjectStatus, DateTimeOffset? HandoverAt, string DataSource, bool MeasuredDataAvailable,
    string Status, SimulatedProductionView? Simulated, IReadOnlyList<string> Alerts, string AlertsStatus, string Notice);

public static class EnergyMonitoringValues
{
    public const string Simulated = "SIMULATED";
    public const string NoMeasuredData = "NO_MEASURED_DATA";
    public const string SimulationUnavailable = "SIMULATION_UNAVAILABLE";
    public const string NotConfigured = "NOT_CONFIGURED";
}

/// <summary>Read-only monitoring view: the customer for their own projects (404 otherwise), Manager/Admin for any project.</summary>
public sealed class EnergyMonitoringHandlers(IInstallationUnitOfWork unitOfWork)
{
    public async Task<OperationResult<EnergyMonitoringView>> GetAsync(Guid userId, bool isStaff, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || (!isStaff && project.CustomerUserId != userId))
            return OperationResult<EnergyMonitoringView>.Fail(InstallationErrorCodes.ProjectNotFound, "Installation project was not found.");
        var simulated = await unitOfWork.GetSelectedSimulationAsync(project.SurveyRequestId, cancellationToken);
        return OperationResult<EnergyMonitoringView>.Ok(new EnergyMonitoringView(project.Id, project.Status, project.HandoverAt,
            EnergyMonitoringValues.Simulated, false, simulated is null ? EnergyMonitoringValues.SimulationUnavailable : EnergyMonitoringValues.NoMeasuredData,
            simulated, [], EnergyMonitoringValues.NotConfigured,
            "Expected production estimated from the selected pre-installation simulation. It is not measured production; no meter data is connected."));
    }
}
