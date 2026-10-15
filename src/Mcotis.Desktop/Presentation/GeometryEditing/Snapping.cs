using System.Windows;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation.GeometryEditing;

/// <summary>What the cursor may snap to while drawing or moving (Draw → Snap Options). User settings of the view, not
/// model data.</summary>
[Flags]
public enum SnapModes
{
    None = 0,
    /// <summary>A point of the model — every member end is one.</summary>
    Point = 1,
    Midpoint = 2,
    /// <summary>Where two members cross in the view.</summary>
    Intersection = 4,
    /// <summary>Where two grid lines, or a grid line and a level, cross.</summary>
    GridIntersection = 8,
    /// <summary>Anywhere along a visible grid line or level.</summary>
    GridLine = 16,
    /// <summary>The foot of the perpendicular from the start of the member being drawn onto a member.</summary>
    Perpendicular = 32,
    /// <summary>The nearest position on a member.</summary>
    Nearest = 64,
    /// <summary>Keep the member being drawn horizontal or vertical in the view (Shift does the same while held).</summary>
    Ortho = 128,
}

/// <summary>The kinds of snap, in the order they win when several are within reach.</summary>
public enum SnapKind { None, Point, Intersection, Midpoint, GridIntersection, Perpendicular, Nearest, GridLine, Ortho }

/// <summary>A position the cursor can snap to: where it is drawn on the screen, where it is in the model, and what it is.
/// <paramref name="Joint"/> names the point of the model it is, if any; <paramref name="Members"/> the members it lies on.</summary>
public sealed record SnapCandidate(SnapKind Kind, Point Screen, Position World, string Label, string? Joint = null, IReadOnlyList<string>? Members = null);

/// <summary>Picks the snap for a cursor position: within the radius, the best kind first, then the nearest. Pure: the view
/// builds the candidates in screen space, so the radius is the same at any zoom.</summary>
public static class SnapResolver
{
    /// <summary>The reach of a snap, in pixels.</summary>
    public const double RadiusPx = 10;

    public const SnapModes Defaults = SnapModes.Point | SnapModes.GridIntersection | SnapModes.Midpoint;

    public static int Rank(SnapKind kind) => kind switch
    {
        SnapKind.Point => 0,
        SnapKind.Intersection => 1,
        SnapKind.Midpoint => 2,
        SnapKind.GridIntersection => 3,
        SnapKind.Perpendicular => 4,
        SnapKind.Nearest => 5,
        SnapKind.GridLine => 6,
        _ => 9,
    };

    public static SnapModes ModeOf(SnapKind kind) => kind switch
    {
        SnapKind.Point => SnapModes.Point,
        SnapKind.Intersection => SnapModes.Intersection,
        SnapKind.Midpoint => SnapModes.Midpoint,
        SnapKind.GridIntersection => SnapModes.GridIntersection,
        SnapKind.Perpendicular => SnapModes.Perpendicular,
        SnapKind.Nearest => SnapModes.Nearest,
        SnapKind.GridLine => SnapModes.GridLine,
        SnapKind.Ortho => SnapModes.Ortho,
        _ => SnapModes.None,
    };

    public static string Describe(SnapKind kind) => kind switch
    {
        SnapKind.Point => "Point",
        SnapKind.Intersection => "Intersection",
        SnapKind.Midpoint => "Midpoint",
        SnapKind.GridIntersection => "Grid intersection",
        SnapKind.Perpendicular => "Perpendicular",
        SnapKind.Nearest => "Nearest",
        SnapKind.GridLine => "On grid line",
        SnapKind.Ortho => "Ortho",
        _ => "Free",
    };

    /// <summary>The candidate the cursor snaps to, or null: only kinds that are on, within <paramref name="radius"/>
    /// pixels; the best kind wins, then the nearer.</summary>
    public static SnapCandidate? Pick(Point cursor, IEnumerable<SnapCandidate> candidates, SnapModes modes, double radius = RadiusPx) =>
        candidates.Where(candidate => (modes & ModeOf(candidate.Kind)) != 0 && (candidate.Screen - cursor).Length <= radius)
            .OrderBy(candidate => Rank(candidate.Kind)).ThenBy(candidate => (candidate.Screen - cursor).Length)
            .FirstOrDefault();

    /// <summary>A segment of the view: a member, a grid line or a level, with the model positions of its ends.</summary>
    public sealed record Segment(string Name, Point A, Point B, Position WorldA, Position WorldB, bool IsMember = true);

    /// <summary>The point of segment a–b nearest <paramref name="p"/> as the fraction along it.</summary>
    public static double Fraction(Point p, Point a, Point b)
    {
        var ab = b - a;
        var length = ab.X * ab.X + ab.Y * ab.Y;
        return length <= double.Epsilon ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / length, 0, 1);
    }

    public static Position Along(Position a, Position b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    public static Point Along(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>Midpoints, nearest positions, perpendicular feet and crossings of the segments near the cursor. Only
    /// segments that pass within the radius are looked at, so a large model costs no more per mouse move than its
    /// neighbourhood. A crossing of two members is reported with both names: drawing to it makes a joint there, which
    /// the analysis connects to both (frames are divided at the joints on them); the label says so.</summary>
    public static IEnumerable<SnapCandidate> AlongSegments(Point cursor, IReadOnlyList<Segment> segments, Point? from, Position? fromWorld, double radius = RadiusPx)
    {
        var near = segments.Where(segment => Distance(cursor, segment.A, segment.B) <= radius).ToArray();
        foreach (var segment in near)
        {
            var kind = segment.IsMember ? SnapKind.Nearest : SnapKind.GridLine;
            var t = Fraction(cursor, segment.A, segment.B);
            yield return new(kind, Along(segment.A, segment.B, t), Along(segment.WorldA, segment.WorldB, t),
                segment.IsMember ? $"Nearest on {segment.Name}" : $"On {segment.Name}", Members: segment.IsMember ? [segment.Name] : null);
            if (segment.IsMember)
            {
                yield return new(SnapKind.Midpoint, Along(segment.A, segment.B, 0.5), Along(segment.WorldA, segment.WorldB, 0.5),
                    $"Midpoint of {segment.Name}", Members: [segment.Name]);
                if (from is { } start && fromWorld is not null)
                {
                    var foot = Fraction(start, segment.A, segment.B);
                    if (foot is > 0 and < 1)
                        yield return new(SnapKind.Perpendicular, Along(segment.A, segment.B, foot), Along(segment.WorldA, segment.WorldB, foot),
                            $"Perpendicular to {segment.Name}", Members: [segment.Name]);
                }
            }
        }
        for (var i = 0; i < near.Length; i++)
            for (var j = i + 1; j < near.Length; j++)
            {
                var (first, second) = (near[i], near[j]);
                if (!first.IsMember || !second.IsMember) continue;
                if (Cross(first.A, first.B, second.A, second.B) is not { } at || at.T is <= 1e-6 or >= 1 - 1e-6 || at.U is <= 1e-6 or >= 1 - 1e-6) continue;
                var world = Along(first.WorldA, first.WorldB, at.T);
                var other = Along(second.WorldA, second.WorldB, at.U);
                // two members that cross in the view but pass each other in depth do not meet: no snap there
                if (Math.Abs(world.X - other.X) + Math.Abs(world.Y - other.Y) + Math.Abs(world.Z - other.Z) > ViewPlanes.Tolerance * 3) continue;
                yield return new(SnapKind.Intersection, Along(first.A, first.B, at.T), world, $"Intersection of {first.Name} and {second.Name}",
                    Members: [first.Name, second.Name]);
            }
    }

    public static double Distance(Point p, Point a, Point b) => (p - Along(a, b, Fraction(p, a, b))).Length;

    /// <summary>Where segments p1–p2 and q1–q2 cross, as the fractions along each, or null when they are parallel.</summary>
    public static (double T, double U)? Cross(Point p1, Point p2, Point q1, Point q2)
    {
        var r = p2 - p1;
        var s = q2 - q1;
        var denominator = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(denominator) < 1e-12) return null;
        var qp = q1 - p1;
        var t = (qp.X * s.Y - qp.Y * s.X) / denominator;
        var u = (qp.X * r.Y - qp.Y * r.X) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? (t, u) : null;
    }

    /// <summary>Ortho: the cursor held to the horizontal or the vertical through the start, whichever is nearer.</summary>
    public static Point Ortho(Point start, Point cursor) =>
        Math.Abs(cursor.X - start.X) >= Math.Abs(cursor.Y - start.Y) ? new(cursor.X, start.Y) : new(start.X, cursor.Y);
}
