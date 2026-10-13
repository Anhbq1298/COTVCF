using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Geometry;

/// <summary>Splits a frame at joints lying along it, as SAP connects frames at intermediate joints.</summary>
public static class FrameJoints
{
    /// <summary>Distance from the frame axis within which a joint is treated as lying on the frame [m].</summary>
    public const double OnFrameToleranceM = 0.001;

    public sealed record Piece(StructuralMember Member, double FractionStart, double FractionEnd);

    /// <summary>
    /// The frame's pieces between consecutive joints from <paramref name="joints"/> that lie on it (in XZ), ordered from
    /// I to J. A frame with no intermediate joint returns itself as one piece.
    /// </summary>
    public static IReadOnlyList<Piece> Pieces(StructuralMember member, IReadOnlyDictionary<string, Position> joints)
    {
        if (!joints.TryGetValue(member.Start, out var start) || !joints.TryGetValue(member.End, out var end)) return [new(member, 0, 1)];
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (length <= OnFrameToleranceM) return [new(member, 0, 1)];
        var minimumFraction = OnFrameToleranceM / length;
        var interior = joints
            .Where(joint => joint.Key != member.Start && joint.Key != member.End)
            .Select(joint =>
            {
                var px = joint.Value.X - start.X;
                var pz = joint.Value.Z - start.Z;
                var along = (px * dx + pz * dz) / length;
                var offset = Math.Abs(px * dz - pz * dx) / length;
                return (joint.Key, Fraction: along / length, Offset: offset, OutOfPlane: Math.Abs(joint.Value.Y - start.Y));
            })
            .Where(item => item.Offset <= OnFrameToleranceM && item.OutOfPlane <= OnFrameToleranceM &&
                           item.Fraction > minimumFraction && item.Fraction < 1 - minimumFraction)
            .OrderBy(item => item.Fraction).ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
        // Coincident joints (within tolerance) would create a zero-length piece; keep the first.
        var stops = new List<(string Name, double Fraction)> { (member.Start, 0) };
        foreach (var item in interior)
            if ((item.Fraction - stops[^1].Fraction) > minimumFraction) stops.Add((item.Key, item.Fraction));
        if (1 - stops[^1].Fraction <= minimumFraction) stops.RemoveAt(stops.Count - 1);
        stops.Add((member.End, 1));
        if (stops.Count == 2) return [new(member, 0, 1)];
        return stops.Zip(stops.Skip(1), (from, to) => new Piece(member with { Start = from.Name, End = to.Name }, from.Fraction, to.Fraction)).ToArray();
    }
}
