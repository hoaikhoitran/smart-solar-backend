using SmartSolar.Modules.SolarSimulation.Geometry;

namespace SmartSolar.Modules.SolarSimulation.Shading;

/// <summary>
/// Geometric shaded fraction of every panel for a sun direction:
/// <c>shadedFraction[i] = blockedSamples[i] / validSamples[i]</c>, always in [0, 1].
/// Each panel's front face is sampled on a regular grid (<c>samplesAlongWidth × samplesAlongLength</c> cell centres, lifted
/// 1 mm along the panel normal so a ray never hits its own panel). Sun directions are grouped in bins of
/// <c>directionBinDegree</c> (elevation × azimuth) and each bin is ray-cast once at its centre and memorised: the scene is
/// static, so the same sun direction always gives the same shadow (an azimuth/altitude shading table, as in NREL SAM).
/// </summary>
public sealed class PanelShadingCalculator
{
    private const double SampleLiftM = 0.001;

    private readonly ShadingScene _scene;
    private readonly IReadOnlyList<PanelFace> _panels;
    private readonly IReadOnlyList<Vec3[]> _samples;
    private readonly double _binDegree;
    private readonly Dictionary<(int Elevation, int Azimuth), double[]> _memo = new();

    public PanelShadingCalculator(ShadingScene scene, IReadOnlyList<PanelFace> panels, int samplesAlongWidth, int samplesAlongLength, double directionBinDegree)
    {
        _scene = scene;
        _panels = panels;
        _binDegree = directionBinDegree;
        _samples = panels.Select(p => SamplePoints(p, samplesAlongWidth, samplesAlongLength)).ToList();
    }

    public IReadOnlyList<PanelFace> Panels => _panels;

    public ShadingScene Scene => _scene;

    public int SamplesPerPanel => _samples.Count == 0 ? 0 : _samples[0].Length;

    /// <summary>Distinct direction bins ray-cast so far.</summary>
    public int CalculatedBins => _memo.Count;

    /// <summary>The bin key of a sun position (index of its elevation and azimuth cell).</summary>
    public (int Elevation, int Azimuth) BinOf(SunPosition sun)
        => ((int)Math.Floor(sun.ElevationDegree / _binDegree), (int)Math.Floor(sun.AzimuthDegree / _binDegree) % (int)Math.Round(360 / _binDegree));

    /// <summary>Shaded fraction (0..1) of each panel, in the order of <see cref="Panels"/>, for the bin containing <paramref name="sun"/>.</summary>
    public double[] FractionsFor(SunPosition sun)
    {
        var bin = BinOf(sun);
        if (_memo.TryGetValue(bin, out var cached)) return cached;

        // Ray-cast at the centre of the bin, so every timestamp falling in it gets the same result.
        var centre = new SunPosition((bin.Elevation + 0.5) * _binDegree, (bin.Azimuth + 0.5) * _binDegree);
        var fractions = centre.ElevationDegree <= 0 ? new double[_panels.Count] : RayCast(centre.Direction);
        _memo[bin] = fractions;
        return fractions;
    }

    /// <summary>Fractions for an exact sun direction, without binning (tests and diagnostics).</summary>
    public double[] RayCast(Vec3 sunDirection)
    {
        var fractions = new double[_panels.Count];
        for (var i = 0; i < _panels.Count; i++)
        {
            var points = _samples[i];
            var blocked = 0;
            foreach (var point in points)
            {
                if (_scene.IsBlocked(point, sunDirection, _panels[i].Index)) blocked++;
            }
            fractions[i] = points.Length == 0 ? 0 : (double)blocked / points.Length;
        }
        return fractions;
    }

    /// <summary>Cell centres of a regular grid on the panel's front face (u along the width, v along the length).</summary>
    public static Vec3[] SamplePoints(PanelFace panel, int alongWidth, int alongLength)
    {
        var points = new Vec3[alongWidth * alongLength];
        var k = 0;
        for (var i = 0; i < alongWidth; i++)
        {
            var u = ((i + 0.5) / alongWidth - 0.5) * panel.WidthM;
            for (var j = 0; j < alongLength; j++)
            {
                var v = ((j + 0.5) / alongLength - 0.5) * panel.LengthM;
                points[k++] = panel.Center + u * panel.WidthAxis + v * panel.UpAxis + SampleLiftM * panel.Normal;
            }
        }
        return points;
    }
}
