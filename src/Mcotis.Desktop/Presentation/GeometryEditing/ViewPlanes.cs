using System.Globalization;
using System.Windows;
using Mcotis.Application;
using Mcotis.Desktop.Domain;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation.GeometryEditing;

/// <summary>One entry of the plane navigator: a named level or grid line, a coordinate the model's points sit at, or the
/// whole model projected (<see cref="Coordinate"/> null).</summary>
public sealed record PlaneChoice(string Label, double? Coordinate, string? ReferenceName = null)
{
    public override string ToString() => Label;
}

/// <summary>
/// The planes of the model view (geometry editing plan, slice 01; first built for the native workspace as
/// NativeGridLevelController on feature/geometry-editing-plan): XY plan at Z = c, XZ elevation at Y = c, YZ elevation at
/// X = c, or 3D. A plane is a view only: it changes what is drawn and where a click lands, never the model or the degrees
/// of freedom of an analysis. A member is shown in a plane only when it lies in it; one that crosses it is not drawn as
/// if it were connected there.
/// </summary>
internal static class ViewPlanes
{
    /// <summary>A point is in a plane within this distance [m]: the model's joint tolerance, so coordinates read from
    /// another program with round-off still sit on their level. Separate from the snap radius, which is in pixels.</summary>
    public const double Tolerance = GeometryEditService.DefaultTolerance;

    public static bool IsPlane(string plane) => plane is "XY" or "XZ" or "YZ";

    /// <summary>The global axis a plane holds constant: Z for XY, Y for XZ, X for YZ.</summary>
    public static string FixedAxis(string plane) => plane switch { "XY" => "Z", "XZ" => "Y", "YZ" => "X", _ => "" };

    public static double Fixed(Position position, string plane) => plane switch
    {
        "XY" => position.Z,
        "XZ" => position.Y,
        "YZ" => position.X,
        _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "3D has no fixed coordinate."),
    };

    /// <summary>The 2D coordinates of a position in a plane: (X, Y), (X, Z) or (Y, Z).</summary>
    public static Point Project(Position position, string plane) => plane switch
    {
        "XY" => new(position.X, position.Y),
        "YZ" => new(position.Y, position.Z),
        _ => new(position.X, position.Z),
    };

    /// <summary>The global position of the 2D point <paramref name="uv"/> of a plane at its fixed coordinate.</summary>
    public static Position ToWorld(Point uv, string plane, double fixedCoordinate) => plane switch
    {
        "XY" => new(uv.X, uv.Y, fixedCoordinate),
        "YZ" => new(fixedCoordinate, uv.X, uv.Y),
        _ => new(uv.X, fixedCoordinate, uv.Y),
    };

    /// <summary>The points of the model in the plane and the members that lie in it (both ends in it). Null or 3D: the
    /// whole model.</summary>
    public static GeometryModel Slice(GeometryModel geometry, string plane, double? coordinate)
    {
        if (!IsPlane(plane) || coordinate is not { } c) return geometry;
        var points = geometry.Points.Where(item => Math.Abs(Fixed(item.Value.Position, plane) - c) <= Tolerance)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (points.Count == geometry.Points.Count) return geometry;
        return new(points, geometry.Members.Where(member => points.ContainsKey(member.Start) && points.ContainsKey(member.End)).ToArray());
    }

    /// <summary>The coordinate a model is shown at when a plane is chosen: the one every point shares (a plane model in its
    /// plane, so nothing is hidden), else none — the whole model projected, as the view always showed it.</summary>
    public static double? DefaultCoordinate(GeometryModel geometry, string plane)
    {
        if (!IsPlane(plane) || geometry.Points.Count == 0) return null;
        var values = geometry.Points.Values.Select(point => Fixed(point.Position, plane)).ToArray();
        return values.Max() - values.Min() <= Tolerance ? values.Average() : null;
    }

    public static string Metres(double value) => value.ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture);

    /// <summary>The navigator's choices for a plane: the whole model projected, the named references — levels for XY,
    /// the grid lines that run along the plane for XZ (Y lines) and YZ (X lines) — and every other coordinate the
    /// model's points sit at, from the lowest; a name wins over a bare coordinate at the same place.</summary>
    public static IReadOnlyList<PlaneChoice> Choices(string plane, Native.ReferenceGeometry reference, GeometryModel geometry, int maximumCoordinates = 200)
    {
        if (!IsPlane(plane)) return [];
        var axis = FixedAxis(plane);
        var named = new List<PlaneChoice>();
        if (plane == "XY")
            named.AddRange(reference.Levels.Select(level => new PlaneChoice($"{level.Name} · Z = {Metres(level.Elevation)} m", level.Elevation, level.Name)));
        else
            foreach (var system in reference.GridSystems.Where(system => system.IsAxisAligned))
                foreach (var line in plane == "XZ" ? system.YLines : system.XLines)
                {
                    var at = plane == "XZ" ? system.Origin.Y + line.Coordinate : system.Origin.X + line.Coordinate;
                    var prefix = reference.GridSystems.Length > 1 ? $"{system.Name} " : "";
                    named.Add(new($"Grid {prefix}{line.Label} · {axis} = {Metres(at)} m", at, line.Label));
                }

        var choices = new List<PlaneChoice>(named);
        foreach (var value in geometry.Points.Values.Select(point => Fixed(point.Position, plane)).OrderBy(value => value))
        {
            if (choices.Any(choice => choice.Coordinate is { } c && Math.Abs(c - value) <= Tolerance)) continue;
            if (choices.Count - named.Count >= maximumCoordinates) break;
            choices.Add(new($"{axis} = {Metres(value)} m", value));
        }
        return [new("All (projection)", null), .. choices.OrderBy(choice => choice.Coordinate)];
    }

    /// <summary>The choice that matches a coordinate (null for the projection), or a typed coordinate that is none of them.</summary>
    public static PlaneChoice ChoiceFor(IReadOnlyList<PlaneChoice> choices, string plane, double? coordinate) =>
        coordinate is not { } c ? choices.FirstOrDefault(choice => choice.Coordinate is null) ?? new("All (projection)", null)
        : choices.FirstOrDefault(choice => choice.Coordinate is { } value && Math.Abs(value - c) <= Tolerance)
          ?? new($"{FixedAxis(plane)} = {Metres(c)} m", c);

    /// <summary>The view's own name, for the canvas and the status bar: "XY plan · L2 · Z = +8.000 m".</summary>
    public static string Describe(string plane, PlaneChoice? choice) => plane switch
    {
        "XY" => "XY plan" + Suffix(choice),
        "XZ" => "XZ elevation" + Suffix(choice),
        "YZ" => "YZ elevation" + Suffix(choice),
        _ => "3D",
    };

    private static string Suffix(PlaneChoice? choice) => choice is null || choice.Coordinate is null ? " · all (projection)" : " · " + choice.Label;
}
