using Mcotis.Desktop.Domain;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation.GeometryEditing;

/// <summary>A grid line or a level as the view draws it: a segment in model coordinates and its label.</summary>
public sealed record ReferenceLine(ReferenceLineKind Kind, string Label, Position A, Position B);

public enum ReferenceLineKind { GridX, GridY, Level }

/// <summary>A crossing of two references (grid × grid in plan, grid × level in an elevation): a snap target that is no
/// point of the model until one is made there.</summary>
public sealed record ReferenceCrossing(string Label, Position At);

/// <summary>
/// What of the grids and levels a plane shows (geometry editing plan, P1): in plan the X and Y lines of every visible
/// system; in an elevation the grid lines that cross it — vertical — and the levels — horizontal. Lines are drawn over a
/// finite extent: the other references, else the model, with a margin; never infinitely. Pure, for the view and the checks.
/// </summary>
internal static class ReferenceLines
{
    public sealed record Layout(IReadOnlyList<ReferenceLine> Lines, IReadOnlyList<ReferenceCrossing> Crossings);

    public static Layout For(Native.ReferenceGeometry reference, string plane, double? coordinate, GeometryModel model)
    {
        var lines = new List<ReferenceLine>();
        var crossings = new List<ReferenceCrossing>();
        if (!ViewPlanes.IsPlane(plane)) return new(lines, crossings);
        var points = model.Points.Values.Select(point => point.Position).ToArray();
        static (double Min, double Max) Range(IEnumerable<double> values, double fallbackMin, double fallbackMax)
        {
            var list = values.ToArray();
            return list.Length == 0 ? (fallbackMin, fallbackMax) : (list.Min(), list.Max());
        }
        static (double Min, double Max) Widen((double Min, double Max) range)
        {
            var margin = Math.Max(1.0, 0.08 * (range.Max - range.Min));
            return (range.Min - margin, range.Max + margin);
        }
        var levels = reference.Levels.Where(level => level.Visible).ToArray();

        if (plane == "XY")
        {
            var z = coordinate ?? levels.Select(level => (double?)level.Elevation).FirstOrDefault() ?? 0;
            foreach (var system in reference.GridSystems.Where(system => system.Visible))
            {
                var xs = system.XLines.Where(line => line.Visible).ToArray();
                var ys = system.YLines.Where(line => line.Visible).ToArray();
                // the extent of each family: the lines of the other, else the model seen in the system's own axes
                var local = points.Select(p => Local(system, p)).ToArray();
                var xRange = Widen(Range(xs.Select(line => line.Coordinate).Concat(ys.Length > 0 && xs.Length == 0 ? local.Select(p => p.X) : []), -5, 5));
                var yRange = Widen(Range(ys.Select(line => line.Coordinate).Concat(xs.Length > 0 && ys.Length == 0 ? local.Select(p => p.Y) : []), -5, 5));
                foreach (var line in xs)
                    lines.Add(new(ReferenceLineKind.GridX, line.Label, At(system.ToGlobal(line.Coordinate, yRange.Min), z), At(system.ToGlobal(line.Coordinate, yRange.Max), z)));
                foreach (var line in ys)
                    lines.Add(new(ReferenceLineKind.GridY, line.Label, At(system.ToGlobal(xRange.Min, line.Coordinate), z), At(system.ToGlobal(xRange.Max, line.Coordinate), z)));
                foreach (var x in xs)
                    foreach (var y in ys)
                        crossings.Add(new($"{y.Label}/{x.Label}", At(system.ToGlobal(x.Coordinate, y.Coordinate), z)));
            }
            return new(lines, crossings);
        }

        // an elevation: where each vertical grid plane meets it, and the levels across it
        var c = coordinate;
        var verticals = new List<(string Label, double U, ReferenceLineKind Kind)>();
        foreach (var system in reference.GridSystems.Where(system => system.Visible))
            foreach (var (line, xLine) in system.XLines.Select(line => (line, true)).Concat(system.YLines.Select(line => (line, false))))
            {
                if (!line.Visible) continue;
                var p0 = xLine ? system.ToGlobal(line.Coordinate, 0) : system.ToGlobal(0, line.Coordinate);
                var p1 = xLine ? system.ToGlobal(line.Coordinate, 1) : system.ToGlobal(1, line.Coordinate);
                var (dx, dy) = (p1.X - p0.X, p1.Y - p0.Y);
                double? u = null;
                if (plane == "XZ")
                {
                    // the grid plane crosses Y = c at one X, or — running along X — is parallel to the elevation
                    if (Math.Abs(dx) < 1e-9) u = p0.X;
                    else if (Math.Abs(dy) > 1e-9 && c is { } y) u = p0.X + (y - p0.Y) / dy * dx;
                }
                else
                {
                    if (Math.Abs(dy) < 1e-9) u = p0.Y;
                    else if (Math.Abs(dx) > 1e-9 && c is { } x) u = p0.Y + (x - p0.X) / dx * dy;
                }
                if (u is { } value) verticals.Add((line.Label, value, xLine ? ReferenceLineKind.GridX : ReferenceLineKind.GridY));
            }
        var horizontal = Widen(verticals.Count > 0
            ? Range(verticals.Select(item => item.U), 0, 0)
            : Range(points.Select(p => plane == "XZ" ? p.X : p.Y), -5, 5));
        var vertical = Widen(levels.Length > 0 ? Range(levels.Select(level => level.Elevation), 0, 0) : Range(points.Select(p => p.Z), 0, 10));
        var fixedValue = c ?? 0;
        Position Elevation(double u, double z) => plane == "XZ" ? new(u, fixedValue, z) : new(fixedValue, u, z);
        foreach (var (label, u, kind) in verticals)
            lines.Add(new(kind, label, Elevation(u, vertical.Min), Elevation(u, vertical.Max)));
        foreach (var level in levels)
            lines.Add(new(ReferenceLineKind.Level, level.Name, Elevation(horizontal.Min, level.Elevation), Elevation(horizontal.Max, level.Elevation)));
        foreach (var (label, u, _) in verticals)
            foreach (var level in levels)
                crossings.Add(new($"{level.Name}/{label}", Elevation(u, level.Elevation)));
        return new(lines, crossings);
    }

    /// <summary>The grid lines of every visible system at one elevation, for the 3D view.</summary>
    public static IReadOnlyList<ReferenceLine> PlanAt(Native.ReferenceGeometry reference, double z, GeometryModel model) =>
        For(reference, "XY", z, model).Lines;

    private static Position At(Native.Xyz p, double z) => new(p.X, p.Y, z);

    private static Native.Xyz Local(Native.GridSystem system, Position p)
    {
        var radians = -system.RotationDeg * Math.PI / 180;
        var (x, y) = (p.X - system.Origin.X, p.Y - system.Origin.Y);
        return new(x * Math.Cos(radians) - y * Math.Sin(radians), x * Math.Sin(radians) + y * Math.Cos(radians), p.Z);
    }
}
