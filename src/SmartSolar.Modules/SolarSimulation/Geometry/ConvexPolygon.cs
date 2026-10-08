namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>Convex polygon in a 2D frame; vertices in order (either winding).</summary>
public sealed class ConvexPolygon
{
    private const double Epsilon = 1e-12;

    public ConvexPolygon(IReadOnlyList<Vec2> vertices)
    {
        Vertices = vertices;
    }

    public IReadOnlyList<Vec2> Vertices { get; }

    public bool IsEmpty => Vertices.Count < 3 || Math.Abs(SignedArea()) < 1e-14;

    public double MinX => Vertices.Min(v => v.X);
    public double MaxX => Vertices.Max(v => v.X);
    public double MinY => Vertices.Min(v => v.Y);
    public double MaxY => Vertices.Max(v => v.Y);

    public static ConvexPolygon FromRect(Rect rect) => new([
        new Vec2(rect.MinX, rect.MinY), new Vec2(rect.MaxX, rect.MinY),
        new Vec2(rect.MaxX, rect.MaxY), new Vec2(rect.MinX, rect.MaxY)]);

    public ConvexPolygon Map(Func<Vec2, Vec2> transform) => new(Vertices.Select(transform).ToList());

    /// <summary>Part of the polygon with minY ≤ y ≤ maxY.</summary>
    public ConvexPolygon ClipToBand(double minY, double maxY)
    {
        var clipped = ClipHalfPlane(Vertices, v => v.Y - minY);
        clipped = ClipHalfPlane(clipped, v => maxY - v.Y);
        return new ConvexPolygon(clipped);
    }

    /// <summary>X interval where the horizontal line at y crosses the polygon, or null when it misses.</summary>
    public (double Min, double Max)? XRangeAt(double y)
    {
        double? min = null, max = null;
        for (var i = 0; i < Vertices.Count; i++)
        {
            var a = Vertices[i];
            var b = Vertices[(i + 1) % Vertices.Count];
            var lowY = Math.Min(a.Y, b.Y);
            var highY = Math.Max(a.Y, b.Y);

            if (y < lowY - Epsilon || y > highY + Epsilon) continue;

            if (Math.Abs(b.Y - a.Y) < Epsilon)
            {
                Extend(a.X); Extend(b.X);
            }
            else
            {
                Extend(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
        }

        return min is null ? null : (min.Value, max!.Value);

        void Extend(double x)
        {
            min = min is null ? x : Math.Min(min.Value, x);
            max = max is null ? x : Math.Max(max.Value, x);
        }
    }

    private double SignedArea()
    {
        double sum = 0;
        for (var i = 0; i < Vertices.Count; i++)
        {
            var a = Vertices[i];
            var b = Vertices[(i + 1) % Vertices.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>Sutherland–Hodgman against one half-plane where inside(v) ≥ 0.</summary>
    private static List<Vec2> ClipHalfPlane(IReadOnlyList<Vec2> polygon, Func<Vec2, double> inside)
    {
        var output = new List<Vec2>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            var dc = inside(current);
            var dn = inside(next);

            if (dc >= 0) output.Add(current);
            if ((dc >= 0) != (dn >= 0))
            {
                var t = dc / (dc - dn);
                output.Add(new Vec2(current.X + t * (next.X - current.X), current.Y + t * (next.Y - current.Y)));
            }
        }
        return output;
    }
}
