namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>2D point in a surface-plane frame, meters.</summary>
public readonly record struct Vec2(double X, double Y);

/// <summary>3D vector in the world frame: X = East, Y = North, Z = Up, meters.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Up = new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(double k, Vec3 a) => new(k * a.X, k * a.Y, k * a.Z);

    public double Dot(Vec3 other) => X * other.X + Y * other.Y + Z * other.Z;

    public Vec3 Cross(Vec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public double Length => Math.Sqrt(Dot(this));

    public Vec3 Normalized()
    {
        var length = Length;
        return length < 1e-12 ? this : (1 / length) * this;
    }
}

/// <summary>Unit quaternion (x, y, z, w), the same component order as Three.js.</summary>
public readonly record struct Quaternion(double X, double Y, double Z, double W)
{
    /// <summary>Rotation whose columns map local X, Y, Z onto the given orthonormal right-handed axes.</summary>
    public static Quaternion FromBasis(Vec3 x, Vec3 y, Vec3 z)
    {
        double m00 = x.X, m01 = y.X, m02 = z.X;
        double m10 = x.Y, m11 = y.Y, m12 = z.Y;
        double m20 = x.Z, m21 = y.Z, m22 = z.Z;
        var trace = m00 + m11 + m22;

        if (trace > 0)
        {
            var s = 0.5 / Math.Sqrt(trace + 1.0);
            return new Quaternion((m21 - m12) * s, (m02 - m20) * s, (m10 - m01) * s, 0.25 / s);
        }

        if (m00 > m11 && m00 > m22)
        {
            var s = 2.0 * Math.Sqrt(1.0 + m00 - m11 - m22);
            return new Quaternion(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }

        if (m11 > m22)
        {
            var s = 2.0 * Math.Sqrt(1.0 + m11 - m00 - m22);
            return new Quaternion((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s);
        }

        var t = 2.0 * Math.Sqrt(1.0 + m22 - m00 - m11);
        return new Quaternion((m02 + m20) / t, (m12 + m21) / t, 0.25 * t, (m10 - m01) / t);
    }
}

internal static class Angles
{
    public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
