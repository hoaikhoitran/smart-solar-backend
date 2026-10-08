namespace SmartSolar.Api.Contracts.Simulations;

/// <summary>Spacing overrides in millimetres (20 mm = 2 cm). Omit a value to use the resolved default.</summary>
public sealed record InstallationSpacingRequest(
    decimal? PanelGapMm,
    decimal? RowGapMm,
    decimal? EdgeSetbackMm,
    decimal? ObstacleClearanceMm,
    decimal? RackLowEdgeClearanceMm);

/// <summary>
/// mountingType: FLUSH (panels follow the surface; omit panel angles) or RACK (absolute panel
/// tilt and compass azimuth required). expectedGeometryVersion comes from GET .../surface.
/// </summary>
public sealed record CreateSimulationRequest(
    int? ExpectedGeometryVersion,
    Guid ProductId,
    string? MountingType,
    decimal? PanelTiltDegree,
    decimal? PanelAzimuthDegree,
    InstallationSpacingRequest? Installation,
    decimal? SystemLossPercent);
