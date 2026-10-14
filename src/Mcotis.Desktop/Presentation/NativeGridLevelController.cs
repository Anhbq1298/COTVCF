using Mcotis.Desktop.Domain;
using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Model-space plan/elevation controller. A plane's fixed coordinate is independent of
/// its 2D projection. This changes visibility only, not the native FEM document.
/// </summary>
internal static class NativeGridLevelController
{
    internal const double Tolerance = 1e-6; // metres: matching a saved model coordinate, not a snap tolerance

    internal static string FixedAxis(string plane) => plane switch
    {
        "XY" => "Z",
        "XZ" => "Y",
        "YZ" => "X",
        "3D" => "",
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    internal static double Coordinate(Xyz p, string plane) => plane switch
    {
        "XY" => p.Z,
        "XZ" => p.Y,
        "YZ" => p.X,
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    /// <summary>Sorted distinct coordinates obtained from native model points; no duplicate reference planes.</summary>
    internal static IReadOnlyList<double> ReferenceCoordinates(ModelDocument model, string plane)
    {
        if (plane == "3D") return [];
        var values = model.Points.Values.Select(p => Coordinate(p.Position, plane)).OrderBy(v => v).ToArray();
        var distinct = new List<double>();
        foreach (var value in values)
            if (distinct.Count == 0 || Math.Abs(distinct[^1] - value) > Tolerance) distinct.Add(value);
        if (distinct.Count == 0) distinct.Add(0);
        return distinct;
    }

    /// <summary>Only whole members in the requested plane are visible. Crossing members are not cut or disconnected.</summary>
    internal static GeometryModel Slice(GeometryModel geometry, string plane, double coordinate)
    {
        if (plane == "3D") return geometry;
        double Axis(StructuralPoint p) => plane switch
        {
            "XY" => p.Position.Z, "XZ" => p.Position.Y, "YZ" => p.Position.X,
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };
        var visible = geometry.Points.Where(p => Math.Abs(Axis(p.Value) - coordinate) <= Tolerance)
            .ToDictionary(p => p.Key, p => p.Value);
        var members = geometry.Members.Where(m => visible.ContainsKey(m.StartPoint) && visible.ContainsKey(m.EndPoint)).ToArray();
        return new GeometryModel(visible, members);
    }
}
