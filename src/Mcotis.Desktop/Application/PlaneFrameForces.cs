using Mcotis.Analysis;
using Mcotis.Desktop.Geometry;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>
/// The axial force, shear and bending moment a plane view draws (geometry editing follow-up, 2026-10-15: "allow to show
/// axial forces, bending and shear" in every plane). The run keeps a frame's six local components at every station; a
/// plane shows the shear in the plane and the moment about the plane's normal. The rule is the one the XZ elevation has
/// always used (<see cref="FemDocumentRun"/>): with <c>n</c> the normal out of the screen — −Y for XZ (X right, Z up), +Z
/// for an XY plan (X right, Y up), +X for a YZ elevation (Y right, Z up) — the moment is <c>M·n</c> and the shear is
/// <c>−(n × axis 1)·F</c>, the actions being those of the part beyond a section on the part before it. So the XZ view
/// gives exactly what it gave, and every plane reads with the same sign. Joints, supports and cables are left as they are.
/// </summary>
internal static class PlaneFrameForces
{
    public static Native.Xyz Normal(string plane) => plane switch
    {
        "XY" => new(0, 0, 1),
        "YZ" => new(1, 0, 0),
        "XZ" => new(0, -1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Only a plane has a normal."),
    };

    /// <summary><paramref name="result"/> with each frame's shear and moment those of <paramref name="plane"/>, from the
    /// native result named <paramref name="name"/> — a load case, or a combination summed term by term. The result as it is
    /// when the run has no six-component result for the name.</summary>
    public static FemAnalysisResult For(FemAnalysisResult result, FemDocumentResults? native, string? name, string plane,
        IReadOnlyList<(string Name, string Definition)> combinations)
    {
        if (native is null || name is null) return result;
        var frames = Frames(native, name, combinations, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (frames is null) return result;
        var normal = Normal(plane);
        var projected = result.Frames.Select(frame =>
        {
            if (native.Names.Frame(frame.MemberName) is not { } id || !frames.TryGetValue(id, out var data) || data.Stations.Length == 0) return frame;
            var perpendicular = normal.Cross(data.Axis1);
            Native.Xyz Global(double[] local, int at) => data.Axis1 * local[at] + data.Axis2 * local[at + 1] + data.Axis3 * local[at + 2];
            var stations = data.Stations.Select(station =>
            {
                var moved = Global(station.Displacements, 0);
                return new FemFrameStation(station.Relative, station.Actions[0], -perpendicular.Dot(Global(station.Actions, 0)),
                    Global(station.Actions, 3).Dot(normal), moved.X, moved.Z) { UYM = moved.Y };
            }).ToArray();
            var (first, last) = (stations[0], stations[^1]);
            return frame with
            {
                AxialStartKN = first.AxialKN, AxialEndKN = last.AxialKN, ShearStartKN = first.ShearKN, ShearEndKN = last.ShearKN,
                MomentStartKNm = first.MomentKNm, MomentEndKNm = last.MomentKNm, Stations = stations,
            };
        }).ToArray();
        return result with { Frames = projected };
    }

    private sealed record Station(double Relative, double[] Actions, double[] Displacements);
    private sealed record FrameData(Native.Xyz Axis1, Native.Xyz Axis2, Native.Xyz Axis3, Station[] Stations);

    private static Dictionary<Native.ObjectId, FrameData>? Frames(FemDocumentResults native, string name,
        IReadOnlyList<(string Name, string Definition)> combinations, HashSet<string> visiting)
    {
        if (native.Static.TryGetValue(name, out var result)) return Of(result);
        var combination = combinations.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));
        if (combination.Name is null || !visiting.Add(name)) return null;
        Dictionary<Native.ObjectId, FrameData>? sum = null;
        foreach (var term in FemLoadDefinition.Parse(combination.Definition))
        {
            var part = Frames(native, term.Name, combinations, visiting);
            if (part is null) return null;
            if (sum is null)
            {
                sum = part.ToDictionary(item => item.Key, item => item.Value with
                {
                    Stations = [.. item.Value.Stations.Select(station => new Station(station.Relative,
                        [.. station.Actions.Select(value => term.Factor * value)], [.. station.Displacements.Select(value => term.Factor * value)]))],
                });
                continue;
            }
            foreach (var (id, frame) in part)
            {
                if (!sum.TryGetValue(id, out var total) || total.Stations.Length != frame.Stations.Length) return null;
                for (var station = 0; station < frame.Stations.Length; station++)
                for (var component = 0; component < 6; component++)
                {
                    total.Stations[station].Actions[component] += term.Factor * frame.Stations[station].Actions[component];
                    total.Stations[station].Displacements[component] += term.Factor * frame.Stations[station].Displacements[component];
                }
            }
        }
        visiting.Remove(name);
        return sum;
    }

    private static Dictionary<Native.ObjectId, FrameData> Of(StaticResult result) => result.Frames.ToDictionary(frame => frame.ParentObjectId,
        frame => new FrameData(frame.Axis1, frame.Axis2, frame.Axis3,
            [.. frame.Segments.SelectMany(segment => segment.Stations).Select(station => new Station(station.Relative, [.. station.Actions], [.. station.LocalDisplacements]))]));
}
