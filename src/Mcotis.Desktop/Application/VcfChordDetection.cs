using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application;

/// <summary>
/// The Tab 02 chord groups (top, middle, bottom) of a VCF model read from SAP2000, for File → Import From SAP2000. The COT
/// models name the chord joints TTrPt…, MTrPt… and BTrPt…; a model that does not is read geometrically: the three highest
/// levels whose joints are linked by horizontal frames, top to bottom. Each group is ordered along X.
/// </summary>
public static class VcfChordDetection
{
    private const double LevelToleranceM = 0.001;

    public sealed record ChordGroups(IReadOnlyList<string> Top, IReadOnlyList<string> Middle, IReadOnlyList<string> Bottom, string Source);

    public static ChordGroups? Detect(GeometryModel geometry)
    {
        IReadOnlyList<string> Named(string prefix) => geometry.Points.Values
            .Where(point => point.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(point => point.Position.X).ThenBy(point => point.Name, StringComparer.Ordinal).Select(point => point.Name).ToArray();
        var (top, middle, bottom) = (Named("TTrPt"), Named("MTrPt"), Named("BTrPt"));
        if (top.Count > 0 && middle.Count > 0 && bottom.Count > 0) return new(top, middle, bottom, "joint names (TTrPt / MTrPt / BTrPt)");

        // geometric: joints on horizontal frames — their ends and the joints lying along them (a chord may be one frame
        // connected at intermediate joints, as SAP2000 allows) — grouped by elevation
        var onChords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in geometry.Members.Where(member => member.Kind == MemberKind.Frame))
            if (geometry.Points.TryGetValue(member.Start, out var start) && geometry.Points.TryGetValue(member.End, out var end) &&
                Math.Abs(start.Position.Z - end.Position.Z) <= LevelToleranceM &&
                Math.Abs(start.Position.X - end.Position.X) + Math.Abs(start.Position.Y - end.Position.Y) > LevelToleranceM)
                foreach (var piece in Geometry.FrameJoints.Pieces(member, geometry.Points.ToDictionary(point => point.Key, point => point.Value.Position, StringComparer.Ordinal)))
                {
                    onChords.Add(piece.Member.Start);
                    onChords.Add(piece.Member.End);
                }
        var levels = onChords.Select(name => geometry.Points[name])
            .GroupBy(point => Math.Round(point.Position.Z / LevelToleranceM))
            .Where(group => group.Count() >= 3)
            .OrderByDescending(group => group.Key)
            .Take(3)
            .Select(group => (IReadOnlyList<string>)group.OrderBy(point => point.Position.X).ThenBy(point => point.Name, StringComparer.Ordinal)
                .Select(point => point.Name).ToArray())
            .ToArray();
        return levels.Length == 3 ? new(levels[0], levels[1], levels[2], "the three highest levels of horizontal frames") : null;
    }
}
