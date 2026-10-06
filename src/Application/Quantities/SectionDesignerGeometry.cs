namespace COT_VCF.Application.Quantities;

internal readonly record struct PlanePoint(double X, double Y);

/// <summary>One Section Designer shape as drawn: its material, its place in the drawing order and its outline as
/// convex pieces that do not overlap each other.</summary>
internal sealed record SectionDesignerShape(string Name, string Material, double ZOrder, IReadOnlyList<PlanePoint[]> Pieces)
{
    /// <summary>The shape as it is drawn: its outside first, then any opening (a tube). The pieces when not given.</summary>
    public IReadOnlyList<PlanePoint[]> Outlines { get; init; } = Pieces;
}

/// <summary>
/// Plane geometry of Section Designer shapes. A shape is held as convex pieces, so the part of a shape that another
/// shape covers can be cut away exactly (convex minus convex is a set of convex pieces) — no meshing, no sampling.
/// </summary>
internal static class SectionDesignerGeometry
{
    public static double Area(IReadOnlyList<PlanePoint> polygon) => Math.Abs(SignedArea(polygon));
    public static double Area(IEnumerable<PlanePoint[]> pieces) => pieces.Sum(piece => Area(piece));

    private static double SignedArea(IReadOnlyList<PlanePoint> polygon)
    {
        var twice = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var next = polygon[(i + 1) % polygon.Count];
            twice += polygon[i].X * next.Y - next.X * polygon[i].Y;
        }
        return twice / 2;
    }

    /// <summary>Counter-clockwise rectangle about its centre.</summary>
    public static PlanePoint[] Rectangle(double centreX, double centreY, double width, double height) =>
    [
        new(centreX - width / 2, centreY - height / 2), new(centreX + width / 2, centreY - height / 2),
        new(centreX + width / 2, centreY + height / 2), new(centreX - width / 2, centreY + height / 2),
    ];

    /// <summary>Outline of an I-shape about the centre of its bounding box, flanges centred on the web.</summary>
    public static PlanePoint[] WideFlange(double centreX, double centreY, double height, double topWidth, double topThick, double web, double bottomWidth, double bottomThick) =>
    [
        new(centreX - bottomWidth / 2, centreY - height / 2), new(centreX + bottomWidth / 2, centreY - height / 2),
        new(centreX + bottomWidth / 2, centreY - height / 2 + bottomThick), new(centreX + web / 2, centreY - height / 2 + bottomThick),
        new(centreX + web / 2, centreY + height / 2 - topThick), new(centreX + topWidth / 2, centreY + height / 2 - topThick),
        new(centreX + topWidth / 2, centreY + height / 2), new(centreX - topWidth / 2, centreY + height / 2),
        new(centreX - topWidth / 2, centreY + height / 2 - topThick), new(centreX - web / 2, centreY + height / 2 - topThick),
        new(centreX - web / 2, centreY - height / 2 + bottomThick), new(centreX - bottomWidth / 2, centreY - height / 2 + bottomThick),
    ];

    /// <summary>A circle as a many-sided outline, for drawing only.</summary>
    public static PlanePoint[] Circle(double centreX, double centreY, double diameter, int sides = 72) =>
        Enumerable.Range(0, sides).Select(i => new PlanePoint(
            centreX + diameter / 2 * Math.Cos(2 * Math.PI * i / sides), centreY + diameter / 2 * Math.Sin(2 * Math.PI * i / sides))).ToArray();

    public static PlanePoint[] Rotate(PlanePoint[] polygon, double centreX, double centreY, double degrees)
    {
        if (degrees == 0) return polygon;
        double cosine = Math.Cos(degrees * Math.PI / 180), sine = Math.Sin(degrees * Math.PI / 180);
        return polygon.Select(point => new PlanePoint(
            centreX + (point.X - centreX) * cosine - (point.Y - centreY) * sine,
            centreY + (point.X - centreX) * sine + (point.Y - centreY) * cosine)).ToArray();
    }

    /// <summary>
    /// The area of each shape that is actually that shape's material: a shape drawn in front (higher Z-order)
    /// replaces what lies behind it, so an embedded steel shape displaces the concrete it sits in. Shapes that
    /// overlap at the same Z-order have no defined owner; that is reported instead of guessed.
    /// </summary>
    public static IReadOnlyDictionary<string, double> VisibleAreas(IReadOnlyList<SectionDesignerShape> shapes, out string? problem)
    {
        problem = null;
        var areas = new Dictionary<string, double>(StringComparer.Ordinal);
        var scale = shapes.SelectMany(shape => shape.Pieces).SelectMany(piece => piece).Select(point => Math.Max(Math.Abs(point.X), Math.Abs(point.Y)))
            .DefaultIfEmpty(1).Max();
        var tolerance = Math.Max(scale, 1e-9) * 1e-9;
        var front = new List<PlanePoint[]>();
        foreach (var level in shapes.GroupBy(shape => shape.ZOrder).OrderByDescending(group => group.Key))
        {
            var members = level.ToArray();
            for (var i = 0; i < members.Length; i++)
            for (var j = i + 1; j < members.Length; j++)
            {
                var apart = Area(Cut(members[i].Pieces, members[j].Pieces, tolerance));
                if (Area(members[i].Pieces) - apart > tolerance * scale)
                    problem ??= $"shapes '{members[i].Name}' and '{members[j].Name}' overlap at the same Z-order ({level.Key:0.###}), so which material fills the overlap is not defined";
            }
            foreach (var shape in members) areas[shape.Name] = Area(Cut(shape.Pieces, front, tolerance));
            front.AddRange(members.SelectMany(shape => shape.Pieces));
        }
        return areas;
    }

    private static List<PlanePoint[]> Cut(IEnumerable<PlanePoint[]> pieces, IReadOnlyList<PlanePoint[]> covers, double tolerance)
    {
        var visible = pieces.ToList();
        foreach (var cover in covers)
        {
            if (visible.Count == 0) break;
            visible = visible.SelectMany(piece => Subtract(piece, cover, tolerance)).ToList();
        }
        return visible;
    }

    /// <summary>Convex <paramref name="subject"/> minus convex counter-clockwise <paramref name="cover"/>: what lies
    /// outside each edge of the cover in turn is kept, what is left inside every edge is the overlap and is dropped.</summary>
    internal static List<PlanePoint[]> Subtract(PlanePoint[] subject, PlanePoint[] cover, double tolerance)
    {
        if (Apart(subject, cover, tolerance)) return [subject];
        var kept = new List<PlanePoint[]>();
        var remaining = subject;
        for (var i = 0; i < cover.Length; i++)
        {
            PlanePoint a = cover[i], b = cover[(i + 1) % cover.Length];
            var outside = Clip(remaining, a, b, keepLeft: false, tolerance);
            if (outside.Length >= 3 && Area(outside) > tolerance * tolerance) kept.Add(outside);
            remaining = Clip(remaining, a, b, keepLeft: true, tolerance);
            if (remaining.Length < 3 || Area(remaining) <= tolerance * tolerance) break;
        }
        return kept;
    }

    private static bool Apart(PlanePoint[] a, PlanePoint[] b, double tolerance) =>
        a.Max(point => point.X) <= b.Min(point => point.X) + tolerance || b.Max(point => point.X) <= a.Min(point => point.X) + tolerance ||
        a.Max(point => point.Y) <= b.Min(point => point.Y) + tolerance || b.Max(point => point.Y) <= a.Min(point => point.Y) + tolerance;

    // Sutherland–Hodgman against one line. A vertex on the line belongs to both sides, so the two halves share it.
    private static PlanePoint[] Clip(PlanePoint[] polygon, PlanePoint a, PlanePoint b, bool keepLeft, double tolerance)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return keepLeft ? polygon : [];
        double Distance(PlanePoint point) => (dx * (point.Y - a.Y) - dy * (point.X - a.X)) / length * (keepLeft ? 1 : -1);
        var result = new List<PlanePoint>(polygon.Length + 2);
        for (var i = 0; i < polygon.Length; i++)
        {
            PlanePoint current = polygon[i], next = polygon[(i + 1) % polygon.Length];
            double here = Distance(current), there = Distance(next);
            if (here >= -tolerance) result.Add(current);
            if ((here > tolerance && there < -tolerance) || (here < -tolerance && there > tolerance))
            {
                var t = here / (here - there);
                result.Add(new(current.X + t * (next.X - current.X), current.Y + t * (next.Y - current.Y)));
            }
        }
        return result.ToArray();
    }

    /// <summary>Convex pieces of a simple polygon (ear clipping), or null when the outline crosses itself or has no
    /// area. A convex outline is returned as it is.</summary>
    public static IReadOnlyList<PlanePoint[]>? ConvexPieces(IReadOnlyList<PlanePoint> outline)
    {
        var points = outline.ToList();
        if (points.Count >= 2 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
        if (points.Count < 3) return null;
        var signed = SignedArea(points);
        var scale = points.Max(point => Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
        if (Math.Abs(signed) <= scale * scale * 1e-12) return null;
        if (signed < 0) points.Reverse();
        if (CrossesItself(points)) return null;
        var tolerance = scale * scale * 1e-12;
        if (Enumerable.Range(0, points.Count).All(i => Cross(points[(i + points.Count - 1) % points.Count], points[i], points[(i + 1) % points.Count]) >= -tolerance))
            return [points.ToArray()];

        var triangles = new List<PlanePoint[]>();
        while (points.Count > 3)
        {
            var clipped = false;
            for (var i = 0; i < points.Count; i++)
            {
                PlanePoint previous = points[(i + points.Count - 1) % points.Count], tip = points[i], next = points[(i + 1) % points.Count];
                var turn = Cross(previous, tip, next);
                if (turn < -tolerance) continue;                                    // reflex corner
                if (turn > tolerance && points.Any(point => point != previous && point != tip && point != next && Inside(point, previous, tip, next, tolerance))) continue;
                if (turn > tolerance) triangles.Add([previous, tip, next]);
                points.RemoveAt(i);                                                 // a straight corner carries no area
                clipped = true;
                break;
            }
            if (!clipped) return null;
        }
        if (Cross(points[0], points[1], points[2]) > tolerance) triangles.Add(points.ToArray());
        return Math.Abs(Area(triangles) - Math.Abs(signed)) <= Math.Abs(signed) * 1e-9 ? triangles : null;
    }

    private static double Cross(PlanePoint a, PlanePoint b, PlanePoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool Inside(PlanePoint point, PlanePoint a, PlanePoint b, PlanePoint c, double tolerance) =>
        Cross(a, b, point) > -tolerance && Cross(b, c, point) > -tolerance && Cross(c, a, point) > -tolerance;

    private static bool CrossesItself(IReadOnlyList<PlanePoint> points)
    {
        for (var i = 0; i < points.Count; i++)
        for (var j = i + 1; j < points.Count; j++)
        {
            if (j == i + 1 || (i == 0 && j == points.Count - 1)) continue;          // neighbouring edges share a corner
            PlanePoint a = points[i], b = points[(i + 1) % points.Count], c = points[j], d = points[(j + 1) % points.Count];
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        }
        return false;
    }
}
