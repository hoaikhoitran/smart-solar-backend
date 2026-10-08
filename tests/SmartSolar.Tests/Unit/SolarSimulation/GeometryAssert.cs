using SmartSolar.Modules.SolarSimulation.Geometry;

namespace SmartSolar.Tests.Unit.SolarSimulation;

internal static class GeometryAssert
{
    public const double Tolerance = 1e-4;

    public static void Near(double expected, double actual, double tolerance = Tolerance)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but was {actual} (±{tolerance}).");

    public static void Near(Vec3 expected, Vec3 actual, double tolerance = Tolerance)
    {
        Near(expected.X, actual.X, tolerance);
        Near(expected.Y, actual.Y, tolerance);
        Near(expected.Z, actual.Z, tolerance);
    }
}
