namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// A sample coordinate used by the scheduling tests as a stand-in for one real commune (see
/// <see cref="SchedulingTestContext.CommuneCodeOf"/>) and its test zone; seeded sites also store it as their latitude/longitude.
/// Scheduling itself never uses coordinates (commune → operational zone and a fixed buffer only).
/// </summary>
public readonly record struct GeoPoint(decimal Latitude, decimal Longitude);
