namespace SmartSolar.Modules.SolarSimulation.Geometry;

public enum LayoutOrientation
{
    /// <summary>Long panel edge up the slope (along the panel's up axis).</summary>
    Portrait,

    /// <summary>Long panel edge along the row (the panel's horizontal axis).</summary>
    Landscape
}

public static class NoPanelsReasons
{
    public const string SetbackConsumesSurface = "SETBACK_CONSUMES_SURFACE";
    public const string PanelLargerThanInstallableRegion = "PANEL_LARGER_THAN_INSTALLABLE_REGION";
    public const string ObstaclesBlockAllPositions = "OBSTACLES_BLOCK_ALL_POSITIONS";
}

/// <summary>Obstacle rectangle in the surface frame (meters, origin upper-left).</summary>
public sealed record LayoutObstacle(double X, double Y, double Width, double Length)
{
    public Rect Rect => new(X, Y, X + Width, Y + Length);
}

public sealed record OrientationCandidate(PanelFootprint Footprint, double RowGapM);

public sealed record LayoutRequest(
    double SurfaceWidthM,
    double SurfaceLengthM,
    double EdgeSetbackM,
    double ObstacleClearanceM,
    double PanelGapM,
    IReadOnlyList<LayoutObstacle> Obstacles,
    IReadOnlyDictionary<LayoutOrientation, OrientationCandidate> Candidates,
    int OffsetSamples,
    int MaxPlacements,
    long MaxWorkUnits = PanelLayoutCalculator.DefaultMaxWorkUnits);

/// <summary>
/// One footprint in the row frame. Equality uses only the stored values, so two
/// identical layouts compare equal.
/// </summary>
public sealed record FootprintPlacement(int Index, double MinS, double MinT, double MaxS, double MaxT, double RowRotationDegree)
{
    private double Theta => RowRotationDegree * Math.PI / 180.0;

    public Vec2 ToLocal(double s, double t)
        => new(s * Math.Cos(Theta) - t * Math.Sin(Theta), s * Math.Sin(Theta) + t * Math.Cos(Theta));

    public Vec2 CenterLocal => ToLocal((MinS + MaxS) / 2, (MinT + MaxT) / 2);

    public IReadOnlyList<Vec2> CornersLocal =>
    [
        ToLocal(MinS, MinT), ToLocal(MaxS, MinT), ToLocal(MaxS, MaxT), ToLocal(MinS, MaxT)
    ];

    public double MinXM => CornersLocal.Min(c => c.X);
    public double MaxXM => CornersLocal.Max(c => c.X);
    public double MinYM => CornersLocal.Min(c => c.Y);
    public double MaxYM => CornersLocal.Max(c => c.Y);
}

public sealed record LayoutAreas(
    double GrossSurfaceAreaM2,
    double ObstacleOccupiedAreaM2,
    double AvailableSurfaceAreaM2,
    double InstallableAreaM2,
    double PanelCoveredAreaM2,
    double TotalModuleAreaM2);

public sealed record LayoutResult(
    LayoutOrientation? Orientation,
    IReadOnlyList<FootprintPlacement> Placements,
    LayoutAreas Areas,
    string? NoPanelsReason,
    bool ExceededMaxPlacements,
    double RowOffsetM,
    bool ExceededComputationBudget = false);

/// <summary>
/// Deterministic best-found preliminary layout (not a proven global optimum).
/// <list type="number">
/// <item>Shrink the surface by the edge setback and grow each obstacle by its clearance.</item>
/// <item>Rotate both into the row frame of each orientation candidate (convex polygons).</item>
/// <item>Try a set of row start offsets: evenly spaced pitch fractions plus offsets aligned to obstacle edges.</item>
/// <item>For each row band, the usable span is where the band lies fully inside the region; obstacle polygons
/// clipped to the band block their exact span; free spans are packed left-aligned.</item>
/// <item>Keep the highest count; ties prefer Portrait, then the smallest offset.</item>
/// </list>
/// Panels may touch boundaries and clearance zones but never overlap them (tolerance 1 µm).
/// Work is bounded: the calculation stops once more than <see cref="LayoutRequest.MaxPlacements"/>
/// panels would fit or <see cref="LayoutRequest.MaxWorkUnits"/> (rows + obstacle clips + placements)
/// is spent, and then returns no placements with the matching flag set.
/// </summary>
public static class PanelLayoutCalculator
{
    private const double Epsilon = 1e-6;

    public const long DefaultMaxWorkUnits = 10_000_000;

    public static LayoutResult Calculate(LayoutRequest request)
    {
        var surface = new Rect(0, 0, request.SurfaceWidthM, request.SurfaceLengthM);
        var region = new Rect(
            request.EdgeSetbackM, request.EdgeSetbackM,
            request.SurfaceWidthM - request.EdgeSetbackM, request.SurfaceLengthM - request.EdgeSetbackM);
        var exclusions = request.Obstacles.Select(o => o.Rect.Inflate(request.ObstacleClearanceM)).ToList();

        var gross = surface.Area;
        var occupied = RectUnion.AreaWithin(surface, request.Obstacles.Select(o => o.Rect));
        var installable = region.IsEmpty ? 0 : region.Area - RectUnion.AreaWithin(region, exclusions);

        // One budget for the whole calculation: rows, obstacle clips and placements all count,
        // so extreme inputs stop early instead of looping or allocating without bound.
        var budget = new WorkBudget(request.MaxPlacements, request.MaxWorkUnits);

        LayoutOrientation? bestOrientation = null;
        List<FootprintPlacement> best = [];
        double bestOffset = 0;

        if (!region.IsEmpty)
        {
            foreach (var orientation in new[] { LayoutOrientation.Portrait, LayoutOrientation.Landscape })
            {
                if (!request.Candidates.TryGetValue(orientation, out var candidate)) continue;

                var (placements, offset) = BestForCandidate(region, exclusions, candidate, request, budget);
                if (budget.Stopped) break;

                if (placements.Count > best.Count)
                {
                    best = placements;
                    bestOrientation = orientation;
                    bestOffset = offset;
                }
            }
        }

        string? reason = null;
        if (!budget.Stopped && best.Count == 0)
        {
            reason = region.IsEmpty
                ? NoPanelsReasons.SetbackConsumesSurface
                : FitsWithoutObstacles(region, request, budget)
                    ? NoPanelsReasons.ObstaclesBlockAllPositions
                    : NoPanelsReasons.PanelLargerThanInstallableRegion;
        }

        if (budget.Stopped)
        {
            // Never present a partial layout as a result.
            return new LayoutResult(null, [], new LayoutAreas(gross, occupied, gross - occupied, Math.Max(0, installable), 0, 0),
                null, budget.PlacementsExceeded, 0, budget.WorkExceeded && !budget.PlacementsExceeded);
        }

        var footprintArea = bestOrientation is { } chosen
            ? request.Candidates[chosen].Footprint.WidthAlongRowM * request.Candidates[chosen].Footprint.DepthAcrossRowM
            : 0;
        var moduleArea = bestOrientation is { } o
            ? request.Candidates[o].Footprint.PhysicalWidthM * request.Candidates[o].Footprint.PhysicalLengthM
            : 0;

        var areas = new LayoutAreas(
            gross,
            occupied,
            gross - occupied,
            Math.Max(0, installable),
            best.Count * footprintArea,
            best.Count * moduleArea);

        return new LayoutResult(bestOrientation, best, areas, reason, false, bestOffset);
    }

    private static bool FitsWithoutObstacles(Rect region, LayoutRequest request, WorkBudget budget)
        => request.Candidates.Values.Any(c => BestForCandidate(region, [], c, request, budget).Placements.Count > 0);

    private static (List<FootprintPlacement> Placements, double Offset) BestForCandidate(
        Rect region, IReadOnlyList<Rect> exclusions, OrientationCandidate candidate, LayoutRequest request, WorkBudget budget)
    {
        var footprint = candidate.Footprint;
        var theta = footprint.RowRotationDegree * Math.PI / 180.0;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        Vec2 ToRow(Vec2 p) => new(p.X * cos + p.Y * sin, -p.X * sin + p.Y * cos);

        var regionPolygon = ConvexPolygon.FromRect(region).Map(ToRow);
        var exclusionPolygons = exclusions.Select(e => ConvexPolygon.FromRect(e).Map(ToRow)).ToList();
        var exclusionBands = exclusionPolygons.Select(p => (Min: p.MinY, Max: p.MaxY)).ToArray();

        var fw = footprint.WidthAlongRowM;
        var fd = footprint.DepthAcrossRowM;
        var pitch = fd + candidate.RowGapM;
        var tMin = regionPolygon.MinY;
        var tMax = regionPolygon.MaxY;

        if (fw <= 0 || fd <= 0 || fd > tMax - tMin + Epsilon) return ([], 0);

        var offsets = CandidateOffsets(tMin, pitch, fd, request.OffsetSamples, exclusionPolygons);

        List<FootprintPlacement> best = [];
        double bestOffset = 0;
        foreach (var offset in offsets)
        {
            var placements = Pack(regionPolygon, exclusionPolygons, exclusionBands, offset, tMax, fw, fd, pitch,
                request.PanelGapM, footprint.RowRotationDegree, budget);
            if (budget.Stopped) return ([], 0);

            if (placements.Count > best.Count)
            {
                best = placements;
                bestOffset = offset - tMin;
            }
        }

        return (best, bestOffset);
    }

    private static List<double> CandidateOffsets(
        double tMin, double pitch, double fd, int samples, IReadOnlyList<ConvexPolygon> exclusions)
    {
        var raw = new List<double>();
        var count = Math.Max(1, samples);
        for (var k = 0; k < count; k++)
        {
            raw.Add(tMin + pitch * k / count);
        }

        foreach (var polygon in exclusions)
        {
            raw.Add(Wrap(polygon.MaxY));
            raw.Add(Wrap(polygon.MinY - fd));
        }

        var sorted = raw.OrderBy(x => x).ToList();
        var unique = new List<double>();
        foreach (var value in sorted)
        {
            if (unique.Count == 0 || value - unique[^1] > 1e-9) unique.Add(value);
        }
        return unique;

        double Wrap(double t)
        {
            var shifted = (t - tMin) % pitch;
            if (shifted < 0) shifted += pitch;
            if (pitch - shifted < 1e-9) shifted = 0;
            return tMin + shifted;
        }
    }

    private static List<FootprintPlacement> Pack(
        ConvexPolygon region, IReadOnlyList<ConvexPolygon> exclusions, (double Min, double Max)[] exclusionBands,
        double firstRow, double tMax, double fw, double fd, double pitch, double gap, double rotationDegree, WorkBudget budget)
    {
        var placements = new List<FootprintPlacement>();

        // Row count is computed up front in floating point and charged before looping, so a
        // tiny pitch is rejected cheaply. Rows are positioned by index, never by repeated
        // addition, so the loop cannot stall when pitch is below the precision of t0.
        var rowCount = Math.Floor((tMax + Epsilon - fd - firstRow) / pitch) + 1;
        if (rowCount < 1) return placements;
        if (!budget.Charge(rowCount)) return placements;

        var rows = (long)rowCount;
        for (long row = 0; row < rows; row++)
        {
            var t0 = firstRow + row * pitch;
            var t1 = t0 + fd;
            var top = region.XRangeAt(t0);
            var bottom = region.XRangeAt(t1);
            if (top is null || bottom is null) continue;

            var allowedMin = Math.Max(top.Value.Min, bottom.Value.Min);
            var allowedMax = Math.Min(top.Value.Max, bottom.Value.Max);
            if (allowedMax - allowedMin < fw - Epsilon) continue;

            var blocked = new List<(double Min, double Max)>();
            for (var i = 0; i < exclusions.Count; i++)
            {
                // Only obstacles whose extent strictly overlaps the band can block it.
                if (exclusionBands[i].Max <= t0 || exclusionBands[i].Min >= t1) continue;
                if (!budget.Charge(1)) return placements;

                // Exact band: an obstacle only touching the band edge clips to zero area and
                // does not block; any overlap, however thin, blocks its exact span.
                var clipped = exclusions[i].ClipToBand(t0, t1);
                if (!clipped.IsEmpty) blocked.Add((clipped.MinX, clipped.MaxX));
            }

            foreach (var (freeMin, freeMax) in FreeSpans(allowedMin, allowedMax, blocked))
            {
                // Double arithmetic, then capped by the remaining room: no 32-bit overflow and
                // never more than one placement beyond the limit is created.
                var fits = Math.Floor((freeMax - freeMin + gap + Epsilon) / (fw + gap));
                if (fits < 1) continue;

                var room = (long)budget.MaxPlacements + 1 - placements.Count;
                var count = (long)Math.Min(fits, room);
                if (!budget.Charge(count)) return placements;

                for (long k = 0; k < count; k++)
                {
                    var s0 = freeMin + k * (fw + gap);
                    placements.Add(new FootprintPlacement(placements.Count, s0, t0, s0 + fw, t1, rotationDegree));
                }

                if (placements.Count > budget.MaxPlacements)
                {
                    budget.PlacementsExceeded = true;
                    return placements;
                }
            }
        }

        return placements;
    }

    /// <summary>Bounds the total work of one layout calculation.</summary>
    private sealed class WorkBudget(int maxPlacements, long maxWorkUnits)
    {
        private double _remaining = maxWorkUnits;

        public int MaxPlacements { get; } = maxPlacements;

        public bool PlacementsExceeded { get; set; }

        public bool WorkExceeded { get; private set; }

        public bool Stopped => PlacementsExceeded || WorkExceeded;

        public bool Charge(double units)
        {
            if (units > _remaining)
            {
                _remaining = 0;
                WorkExceeded = true;
                return false;
            }

            _remaining -= units;
            return true;
        }
    }

    private static IEnumerable<(double Min, double Max)> FreeSpans(
        double min, double max, List<(double Min, double Max)> blocked)
    {
        var cursor = min;
        foreach (var (bMin, bMax) in blocked.OrderBy(b => b.Min))
        {
            if (bMax <= cursor) continue;
            if (bMin >= max) break;
            if (bMin > cursor) yield return (cursor, bMin);
            cursor = Math.Max(cursor, bMax);
        }
        if (cursor < max) yield return (cursor, max);
    }
}
