namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>
/// Local frame of the rectangular installation surface.
/// <list type="bullet">
/// <item>Origin: upper-left corner in the 2D editor, which is the high edge of an inclined surface.</item>
/// <item>X: along the surface width (to the right in the editor).</item>
/// <item>Y: along the surface length (downward in the editor), pointing down-slope toward the surface azimuth.</item>
/// <item>Distances are meters measured on the surface plane itself.</item>
/// </list>
/// Like screen coordinates the frame (X, Y, Normal) is left-handed, so it is used to
/// transform points only; panels carry their own right-handed rotation.
/// For a horizontal surface the azimuth is simply the compass bearing of +Y.
/// </summary>
public sealed record SurfaceFrame(double TiltDegree, double AzimuthDegree)
{
    /// <summary>World direction of local +X = horizontal bearing (azimuth - 90°).</summary>
    public Vec3 XAxis => AzimuthConvention.HorizontalDirection(AzimuthDegree - 90);

    /// <summary>World direction of local +Y (down-slope).</summary>
    public Vec3 YAxis
    {
        get
        {
            var tilt = Angles.ToRadians(TiltDegree);
            return Math.Cos(tilt) * AzimuthConvention.HorizontalDirection(AzimuthDegree) - Math.Sin(tilt) * Vec3.Up;
        }
    }

    /// <summary>Outward (upper side) unit normal of the surface.</summary>
    public Vec3 Normal => YAxis.Cross(XAxis);

    /// <summary>World position (relative to the surface origin) of a local point lifted along the normal.</summary>
    public Vec3 ToWorld(double x, double y, double heightAlongNormal)
        => x * XAxis + y * YAxis + heightAlongNormal * Normal;

    /// <summary>Local surface coordinates (x, y) of a world vector, dropping the normal component.</summary>
    public Vec2 ToLocal(Vec3 world) => new(world.Dot(XAxis), world.Dot(YAxis));
}

/// <summary>
/// Absolute panel orientation in the world frame. Tilt is from horizontal; azimuth is the
/// compass bearing of the panel's front normal. The panel frame (WidthAxis, UpAxis, Normal)
/// is right-handed: WidthAxis is horizontal, UpAxis climbs the panel slope.
/// </summary>
public sealed record PanelOrientation(double TiltDegree, double AzimuthDegree)
{
    public Vec3 Normal
    {
        get
        {
            var tilt = Angles.ToRadians(TiltDegree);
            var azimuth = Angles.ToRadians(AzimuthDegree);
            return new Vec3(Math.Sin(tilt) * Math.Sin(azimuth), Math.Sin(tilt) * Math.Cos(azimuth), Math.Cos(tilt));
        }
    }

    public Vec3 WidthAxis => AzimuthConvention.HorizontalDirection(AzimuthDegree - 90);

    public Vec3 UpAxis => Normal.Cross(WidthAxis);

    public Quaternion ToQuaternion() => Quaternion.FromBasis(WidthAxis, UpAxis, Normal);
}
