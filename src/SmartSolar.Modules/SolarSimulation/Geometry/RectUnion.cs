namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>Axis-aligned rectangle in a surface frame, meters.</summary>
public readonly record struct Rect(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => Math.Max(0, MaxX - MinX);
    public double Height => Math.Max(0, MaxY - MinY);
    public double Area => Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Rect Intersect(Rect other) => new(
        Math.Max(MinX, other.MinX), Math.Max(MinY, other.MinY),
        Math.Min(MaxX, other.MaxX), Math.Min(MaxY, other.MaxY));

    public Rect Inflate(double amount) => new(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);
}

/// <summary>Exact union area of axis-aligned rectangles by coordinate compression.</summary>
public static class RectUnion
{
    public static double Area(IEnumerable<Rect> rects)
    {
        var list = rects.Where(r => !r.IsEmpty).ToList();
        if (list.Count == 0) return 0;

        var xs = list.SelectMany(r => new[] { r.MinX, r.MaxX }).Distinct().OrderBy(x => x).ToArray();
        var ys = list.SelectMany(r => new[] { r.MinY, r.MaxY }).Distinct().OrderBy(y => y).ToArray();
        double area = 0;

        for (var i = 0; i < xs.Length - 1; i++)
        {
            var cx = (xs[i] + xs[i + 1]) / 2;
            for (var j = 0; j < ys.Length - 1; j++)
            {
                var cy = (ys[j] + ys[j + 1]) / 2;
                if (list.Any(r => r.MinX < cx && cx < r.MaxX && r.MinY < cy && cy < r.MaxY))
                {
                    area += (xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]);
                }
            }
        }

        return area;
    }

    /// <summary>Union area of the parts of <paramref name="rects"/> that fall inside <paramref name="region"/>.</summary>
    public static double AreaWithin(Rect region, IEnumerable<Rect> rects)
        => region.IsEmpty ? 0 : Area(rects.Select(r => r.Intersect(region)));
}
