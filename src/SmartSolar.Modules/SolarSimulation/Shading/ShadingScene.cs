using SmartSolar.Modules.SolarSimulation.Geometry;

namespace SmartSolar.Modules.SolarSimulation.Shading;

/// <summary>
/// An obstacle as a box standing on the installation surface: footprint [X0, X1] × [Y0, Y1] in local surface meters and
/// <see cref="HeightM"/> along the surface normal (the same convention as the 2D editor and the 3D rendering).
/// </summary>
public sealed record ObstacleBox(double X0, double Y0, double X1, double Y1, double HeightM);

/// <summary>
/// One panel's front face in the world frame (X = East, Y = North, Z = Up, meters, origin at the surface origin):
/// <see cref="Center"/> is the front-face centre, <see cref="WidthAxis"/> / <see cref="UpAxis"/> / <see cref="Normal"/> the
/// panel's right-handed unit axes, and the face spans ±Width/2 along WidthAxis and ±Length/2 along UpAxis.
/// </summary>
public sealed record PanelFace(int Index, Vec3 Center, Vec3 WidthAxis, Vec3 UpAxis, Vec3 Normal, double WidthM, double LengthM, double RatedPowerW);

/// <summary>
/// The static 3D scene used for ray casting: the surface rectangle, obstacle boxes and (for RACK layouts) the panels
/// themselves. A ray from a point toward the sun is blocked when it hits any of them; overlapping occluders are never
/// counted twice because the result per ray is just "blocked or not".
/// </summary>
public sealed class ShadingScene
{
    private const double Epsilon = 1e-6;

    private readonly SurfaceFrame _surface;
    private readonly double _surfaceWidthM;
    private readonly double _surfaceLengthM;
    private readonly IReadOnlyList<ObstacleBox> _obstacles;
    private readonly IReadOnlyList<PanelFace> _panelOccluders;

    /// <param name="panelOccluders">Panels that can shade other panels (RACK rows). Empty for FLUSH: coplanar panels cannot shade each other.</param>
    public ShadingScene(SurfaceFrame surface, double surfaceWidthM, double surfaceLengthM, IReadOnlyList<ObstacleBox> obstacles,
        IReadOnlyList<PanelFace> panelOccluders)
    {
        _surface = surface;
        _surfaceWidthM = surfaceWidthM;
        _surfaceLengthM = surfaceLengthM;
        _obstacles = obstacles;
        _panelOccluders = panelOccluders;
    }

    /// <summary>Number of things a ray is tested against (used for the computation budget).</summary>
    public int OccluderCount => _obstacles.Count + _panelOccluders.Count + 1;

    /// <summary>
    /// True when the ray from <paramref name="point"/> toward the sun (<paramref name="sunDirection"/>, unit vector) hits the
    /// surface, an obstacle, or a panel other than <paramref name="ownPanelIndex"/> before leaving the scene.
    /// </summary>
    public bool IsBlocked(Vec3 point, Vec3 sunDirection, int ownPanelIndex)
    {
        // Work in the local surface frame (x, y, height along the normal): obstacles are axis-aligned boxes there.
        var origin = new Vec3(point.Dot(_surface.XAxis), point.Dot(_surface.YAxis), point.Dot(_surface.Normal));
        var direction = new Vec3(sunDirection.Dot(_surface.XAxis), sunDirection.Dot(_surface.YAxis), sunDirection.Dot(_surface.Normal));

        if (HitsSurface(origin, direction)) return true;
        foreach (var box in _obstacles)
        {
            if (HitsBox(origin, direction, box)) return true;
        }
        foreach (var panel in _panelOccluders)
        {
            if (panel.Index != ownPanelIndex && HitsPanel(point, sunDirection, panel)) return true;
        }
        return false;
    }

    /// <summary>The ray goes down through the surface rectangle (the sun is behind the roof plane at that point).</summary>
    private bool HitsSurface(Vec3 origin, Vec3 direction)
    {
        if (direction.Z >= -Epsilon || origin.Z <= Epsilon) return false;
        var t = -origin.Z / direction.Z;
        var x = origin.X + t * direction.X;
        var y = origin.Y + t * direction.Y;
        return x >= 0 && x <= _surfaceWidthM && y >= 0 && y <= _surfaceLengthM;
    }

    /// <summary>Slab method: the ray (t &gt; 0) enters the box before it leaves it on all three local axes.</summary>
    private static bool HitsBox(Vec3 origin, Vec3 direction, ObstacleBox box)
    {
        if (box.HeightM <= 0) return false;
        var tEnter = Epsilon;
        var tExit = double.MaxValue;
        if (!Slab(origin.X, direction.X, box.X0, box.X1, ref tEnter, ref tExit)) return false;
        if (!Slab(origin.Y, direction.Y, box.Y0, box.Y1, ref tEnter, ref tExit)) return false;
        if (!Slab(origin.Z, direction.Z, 0, box.HeightM, ref tEnter, ref tExit)) return false;
        return tEnter <= tExit;
    }

    private static bool Slab(double origin, double direction, double min, double max, ref double tEnter, ref double tExit)
    {
        if (Math.Abs(direction) < 1e-12) return origin >= min && origin <= max;
        var t1 = (min - origin) / direction;
        var t2 = (max - origin) / direction;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tEnter = Math.Max(tEnter, t1);
        tExit = Math.Min(tExit, t2);
        return tEnter <= tExit;
    }

    /// <summary>Ray–rectangle test: intersect the panel plane, then check the hit lies inside the face.</summary>
    private static bool HitsPanel(Vec3 point, Vec3 direction, PanelFace panel)
    {
        var denominator = direction.Dot(panel.Normal);
        if (Math.Abs(denominator) < 1e-12) return false;
        var t = (panel.Center - point).Dot(panel.Normal) / denominator;
        if (t <= Epsilon) return false;
        var hit = point + t * direction - panel.Center;
        return Math.Abs(hit.Dot(panel.WidthAxis)) <= panel.WidthM / 2 && Math.Abs(hit.Dot(panel.UpAxis)) <= panel.LengthM / 2;
    }
}
