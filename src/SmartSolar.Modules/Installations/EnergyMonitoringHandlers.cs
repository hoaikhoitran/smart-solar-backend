using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.IotMonitoring;

namespace SmartSolar.Modules.Installations;

/// <summary>Expected production from the customer's selected simulation (a pre-installation estimate, never a measurement).</summary>
public sealed record SimulatedProductionView(Guid SimulationId, DateTimeOffset SelectedAt, string AlgorithmVersion, string EnergyStatus, int PanelCount,
    decimal InstalledCapacityKwp, decimal? AnnualEnergyKwh, decimal? SpecificYieldKwhPerKwpYear);

/// <summary>
/// Energy monitoring of one installation. Two clearly separated sources: <see cref="Simulated"/> (<see cref="DataSource"/> = <c>SIMULATED</c>, the
/// pre-installation estimate) and <see cref="Measured"/> (<see cref="MeasuredDataSource"/> = <c>IOT_DEVICE</c>, readings of the bound device; null
/// while no device is bound). <see cref="MeasuredDataAvailable"/> is true only when the bound device has delivered a reading; then
/// <see cref="Status"/> is <c>MEASURED_DATA_AVAILABLE</c>, otherwise <c>NO_MEASURED_DATA</c> (or <c>SIMULATION_UNAVAILABLE</c> when there is no
/// simulation either). The V1 hardware measures the current only; power and energy stay NOT_AVAILABLE and are never derived. No
/// abnormal-production alert can be raised yet (<see cref="AlertsStatus"/> = <c>NOT_CONFIGURED</c>: no approved thresholds).
/// </summary>
public sealed record EnergyMonitoringView(Guid ProjectId, string ProjectStatus, DateTimeOffset? HandoverAt, string DataSource, bool MeasuredDataAvailable,
    string Status, SimulatedProductionView? Simulated, IReadOnlyList<string> Alerts, string AlertsStatus, string Notice,
    string? MeasuredDataSource = null, IotMeasuredMonitoringView? Measured = null);

public static class EnergyMonitoringValues
{
    public const string Simulated = "SIMULATED";
    public const string NoMeasuredData = "NO_MEASURED_DATA";
    public const string MeasuredDataAvailable = "MEASURED_DATA_AVAILABLE";
    public const string SimulationUnavailable = "SIMULATION_UNAVAILABLE";
    public const string NotConfigured = "NOT_CONFIGURED";

    public const string SimulatedOnlyNotice =
        "Expected production estimated from the selected pre-installation simulation. It is not measured production; no meter data is connected.";

    public const string WithDeviceNotice =
        "Expected production (simulated) is estimated from the selected pre-installation simulation and is never a measurement. 'measured' carries the readings "
        + "of the bound IoT device: only the current (A) is measured; power, energy and grid import/export are NOT_AVAILABLE until the hardware measures them.";
}

/// <summary>Read-only monitoring view: the customer for their own projects (404 otherwise), Manager/Admin for any project.</summary>
public sealed class EnergyMonitoringHandlers(IInstallationUnitOfWork unitOfWork, IotTelemetryHandlers? telemetry = null)
{
    public async Task<OperationResult<EnergyMonitoringView>> GetAsync(Guid userId, bool isStaff, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || (!isStaff && project.CustomerUserId != userId))
            return OperationResult<EnergyMonitoringView>.Fail(InstallationErrorCodes.ProjectNotFound, "Installation project was not found.");
        var simulated = await unitOfWork.GetSelectedSimulationAsync(project.SurveyRequestId, cancellationToken);
        var measured = telemetry is null ? null : await telemetry.MeasuredAsync(project.Id, cancellationToken);
        var available = measured?.Latest is not null;
        var status = available ? EnergyMonitoringValues.MeasuredDataAvailable
            : simulated is null ? EnergyMonitoringValues.SimulationUnavailable : EnergyMonitoringValues.NoMeasuredData;
        return OperationResult<EnergyMonitoringView>.Ok(new EnergyMonitoringView(project.Id, project.Status, project.HandoverAt, EnergyMonitoringValues.Simulated, available,
            status, simulated, [], EnergyMonitoringValues.NotConfigured, measured is null ? EnergyMonitoringValues.SimulatedOnlyNotice : EnergyMonitoringValues.WithDeviceNotice,
            measured is null ? null : IotViewMapping.MeasuredSource, measured));
    }
}
