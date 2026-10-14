using Mcotis.Desktop.Geometry;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application.Design;

/// <summary>
/// The forces column design takes from a run of the native document (M7 of the main-window migration; task of 2026-10-14,
/// §2 "Design reads results through the document" and "3D forces in design"): every station of every frame in the frame's
/// local axes — F1, F2, F3, M1, M2, M3 as the solver gives them — by the frame's identity, named through the run's
/// <see cref="LegacyNames"/>. A load case is the solver's result; a combination is the sum of its terms, each times its
/// factor — the rule <see cref="FemResultCombiner"/> combines the planar results with. Null when the run has no
/// six-component result for the name (a run restored from an output of schema 1): design then takes the planar stations.
/// </summary>
internal static class NativeDesignForces
{
    /// <summary>The stations of each frame under <paramref name="name"/>, by the frame's name in the application.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<DesignStation>>? Of(FemDocumentResults native, string name,
        IReadOnlyList<(string Name, string Definition)> combinations)
    {
        ArgumentNullException.ThrowIfNull(native);
        var actions = Actions(native, name, combinations, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (actions is null) return null;
        var stations = new Dictionary<string, IReadOnlyList<DesignStation>>(StringComparer.Ordinal);
        foreach (var (id, values) in actions)
            if (native.Names.TryGetName(id, out var frame))
                stations[frame] = values.Select(a => new DesignStation(a[0], a[1], a[5], a[2], a[4], a[3])).ToArray();
        return stations;
    }

    private static Dictionary<Native.ObjectId, double[][]>? Actions(FemDocumentResults native, string name,
        IReadOnlyList<(string Name, string Definition)> combinations, HashSet<string> visiting)
    {
        if (native.Static.TryGetValue(name, out var result))
            return result.Frames.ToDictionary(frame => frame.ParentObjectId,
                frame => frame.Segments.SelectMany(segment => segment.Stations).Select(station => station.Actions.ToArray()).ToArray());
        var combination = combinations.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));
        if (combination.Name is null || !visiting.Add(name)) return null;
        Dictionary<Native.ObjectId, double[][]>? sum = null;
        foreach (var term in FemLoadDefinition.Parse(combination.Definition))
        {
            var part = Actions(native, term.Name, combinations, visiting);
            if (part is null) return null;
            if (sum is null)
            {
                sum = part.ToDictionary(item => item.Key, item => item.Value.Select(values => values.Select(value => term.Factor * value).ToArray()).ToArray());
                continue;
            }
            foreach (var (id, stations) in part)
            {
                if (!sum.TryGetValue(id, out var total) || total.Length != stations.Length) return null;
                for (var station = 0; station < stations.Length; station++)
                for (var component = 0; component < 6; component++)
                    total[station][component] += term.Factor * stations[station][component];
            }
        }
        visiting.Remove(name);
        return sum;
    }
}
