using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>What a member is in the VCF (Vierendeel cable frame) system, read from the Tab 02 chord groups.</summary>
public enum VcfMemberRole { TopChord, MiddleChord, BottomChord, VerticalChord, MegaColumn, EndFrame, Cable }

/// <summary>Edit → VCF Geometry parameters [m]. <see cref="BackSpan"/> / <see cref="BackStayHeight"/> are null when the
/// model has no end frame outside the truss / no node level between the ground and the bottom chord.</summary>
public sealed record VcfGeometryParameters(double BayWidth, double TrussDepth, double MiddleChordHeight, double ClearHeight,
    double? BackSpan, double? BackStayHeight);

/// <summary>The VCF system read from a model: its levels, truss bay lines, end frames and the role of every member.</summary>
public sealed record VcfGeometryAnalysis(
    GeometryModel Geometry,
    int Bays,
    bool UniformBays,
    VcfGeometryParameters Parameters,
    double Ground,
    IReadOnlyList<double> TrussLines,
    double? OuterLeft,
    double? OuterRight,
    bool HasMiddleChord,
    IReadOnlyDictionary<string, VcfMemberRole> Roles);

/// <summary>
/// Edit → VCF Geometry: reads the system from the model and rebuilds it from a few parameters. The topology and every
/// point / member name are kept — nodes are moved by a piecewise-linear map of their X (end-frame outer line, truss bay
/// lines) and Z (ground, back-stay level, bottom / middle / top chord), so Tabs 01–05 and the SAP loads still refer to the
/// same objects. Y is unchanged. Presentation-independent and never touches SAP2000.
/// </summary>
public static class VcfGeometry
{
    private const double Tolerance = 1e-3;

    public static VcfGeometryAnalysis Analyze(GeometryModel geometry, TrussHeightConfiguration truss)
    {
        if (geometry.Points.Count == 0) throw new InvalidOperationException("The model has no geometry.");
        if (truss.TopPoints.Count == 0 || truss.BottomPoints.Count == 0)
            throw new InvalidOperationException("Define the top, middle and bottom chord point groups on 02 Truss Height first.");
        var missing = truss.TopPoints.Concat(truss.MiddlePoints).Concat(truss.BottomPoints).FirstOrDefault(name => !geometry.Points.ContainsKey(name));
        if (missing is not null) throw new InvalidOperationException($"Chord point '{missing}' (02 Truss Height) is not in the model.");

        double Level(IEnumerable<string> names) => names.Select(name => geometry.Points[name].Position.Z).Average();
        var top = Level(truss.TopPoints);
        var bottom = Level(truss.BottomPoints);
        var hasMiddle = truss.MiddlePoints.Count > 0;
        var middle = hasMiddle ? Level(truss.MiddlePoints) : (top + bottom) / 2;
        if (top - bottom <= Tolerance) throw new InvalidOperationException("The top chord must lie above the bottom chord.");
        var ground = geometry.Points.Values.Min(point => point.Position.Z);

        var trussPoints = truss.TopPoints.Concat(truss.MiddlePoints).Concat(truss.BottomPoints).ToHashSet(StringComparer.Ordinal);
        var lines = Distinct(trussPoints.Select(name => geometry.Points[name].Position.X));
        if (lines.Count < 2) throw new InvalidOperationException("The chord points need at least two bay lines.");
        var bays = lines.Count - 1;
        var bay = (lines[^1] - lines[0]) / bays;
        var uniform = Enumerable.Range(1, bays).All(index => Math.Abs(lines[index] - lines[index - 1] - bay) < Tolerance);

        var xs = geometry.Points.Values.Select(point => point.Position.X).ToArray();
        double? outerLeft = xs.Min() < lines[0] - Tolerance ? xs.Min() : null;
        double? outerRight = xs.Max() > lines[^1] + Tolerance ? xs.Max() : null;
        double? backSpan = outerLeft is { } left ? lines[0] - left : outerRight is { } right ? right - lines[^1] : null;
        var between = Distinct(geometry.Points.Values.Select(point => point.Position.Z).Where(z => z > ground + Tolerance && z < bottom - Tolerance));
        double? backStay = between.Count > 0 ? between[^1] - ground : null;

        var roles = new Dictionary<string, VcfMemberRole>(StringComparer.Ordinal);
        var topSet = truss.TopPoints.ToHashSet(StringComparer.Ordinal);
        var middleSet = truss.MiddlePoints.ToHashSet(StringComparer.Ordinal);
        var bottomSet = truss.BottomPoints.ToHashSet(StringComparer.Ordinal);
        foreach (var member in geometry.Members)
        {
            if (member.Kind != MemberKind.Frame) { roles[member.Name] = VcfMemberRole.Cable; continue; }
            if (!geometry.Points.TryGetValue(member.Start, out var a) || !geometry.Points.TryGetValue(member.End, out var b)) continue;
            bool Both(HashSet<string> set) => set.Contains(member.Start) && set.Contains(member.End);
            var vertical = Math.Abs(a.Position.X - b.Position.X) < Tolerance;
            var atEnd = Math.Abs(a.Position.X - lines[0]) < Tolerance || Math.Abs(a.Position.X - lines[^1]) < Tolerance;
            roles[member.Name] =
                Both(topSet) ? VcfMemberRole.TopChord :
                Both(middleSet) ? VcfMemberRole.MiddleChord :
                Both(bottomSet) ? VcfMemberRole.BottomChord :
                vertical && atEnd ? VcfMemberRole.MegaColumn :
                trussPoints.Contains(member.Start) && trussPoints.Contains(member.End) ? VcfMemberRole.VerticalChord :
                VcfMemberRole.EndFrame;
        }

        return new VcfGeometryAnalysis(geometry, bays, uniform,
            new VcfGeometryParameters(bay, top - bottom, middle - bottom, bottom - ground, backSpan, backStay),
            ground, lines, outerLeft, outerRight, hasMiddle, roles);
    }

    public static IReadOnlyList<string> Validate(VcfGeometryAnalysis analysis, VcfGeometryParameters p)
    {
        var problems = new List<string>();
        static bool Positive(double value) => double.IsFinite(value) && value > Tolerance;
        if (!Positive(p.BayWidth)) problems.Add("Bay width must be greater than zero.");
        if (!Positive(p.TrussDepth)) problems.Add("Truss depth must be greater than zero.");
        if (analysis.HasMiddleChord && (!Positive(p.MiddleChordHeight) || p.MiddleChordHeight >= p.TrussDepth - Tolerance))
            problems.Add("Middle chord height must be above the bottom chord and below the top chord.");
        if (!Positive(p.ClearHeight)) problems.Add("Clear height must be greater than zero.");
        if (analysis.Parameters.BackSpan is not null && !(p.BackSpan is { } span && Positive(span))) problems.Add("Back-span must be greater than zero.");
        if (analysis.Parameters.BackStayHeight is not null &&
            !(p.BackStayHeight is { } stay && Positive(stay) && stay < p.ClearHeight - Tolerance))
            problems.Add("Back-stay height must be above the ground and below the bottom chord (clear height).");
        return problems;
    }

    /// <summary>The model rebuilt with <paramref name="p"/>: same points and members, new coordinates.</summary>
    public static GeometryModel Build(VcfGeometryAnalysis analysis, VcfGeometryParameters p)
    {
        var problems = Validate(analysis, p);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        var old = analysis.Parameters;
        var ground = analysis.Ground;

        // Z: ground, back-stay level, bottom / middle / top chord.
        var zOld = new List<double> { ground };
        var zNew = new List<double> { ground };
        if (old.BackStayHeight is { } stay && p.BackStayHeight is { } newStay) { zOld.Add(ground + stay); zNew.Add(ground + newStay); }
        var bottomOld = ground + old.ClearHeight;
        var bottomNew = ground + p.ClearHeight;
        zOld.Add(bottomOld); zNew.Add(bottomNew);
        if (analysis.HasMiddleChord) { zOld.Add(bottomOld + old.MiddleChordHeight); zNew.Add(bottomNew + p.MiddleChordHeight); }
        zOld.Add(bottomOld + old.TrussDepth); zNew.Add(bottomNew + p.TrussDepth);

        // X: end-frame outer lines and the truss bay lines, centred on the truss centre.
        var lines = analysis.TrussLines;
        var centre = (lines[0] + lines[^1]) / 2;
        var xOld = new List<double>();
        var xNew = new List<double>();
        var first = centre - p.BayWidth * analysis.Bays / 2;
        if (analysis.OuterLeft is { } left) { xOld.Add(left); xNew.Add(first - (p.BackSpan ?? lines[0] - left)); }
        for (var index = 0; index < lines.Count; index++) { xOld.Add(lines[index]); xNew.Add(first + index * p.BayWidth); }
        if (analysis.OuterRight is { } right) { xOld.Add(right); xNew.Add(first + analysis.Bays * p.BayWidth + (p.BackSpan ?? right - lines[^1])); }

        var points = analysis.Geometry.Points.ToDictionary(pair => pair.Key, pair =>
        {
            var position = pair.Value.Position;
            return pair.Value with { Position = new Position(Map(position.X, xOld, xNew), position.Y, Map(position.Z, zOld, zNew)) };
        }, StringComparer.Ordinal);
        return new GeometryModel(points, analysis.Geometry.Members);
    }

    /// <summary>Piecewise-linear map through the breakpoints; beyond the ends a value keeps its distance to the end.</summary>
    private static double Map(double value, IReadOnlyList<double> from, IReadOnlyList<double> to)
    {
        if (value <= from[0]) return to[0] + (value - from[0]);
        for (var index = 1; index < from.Count; index++)
            if (value <= from[index])
            {
                var span = from[index] - from[index - 1];
                var t = span > 0 ? (value - from[index - 1]) / span : 0;
                return to[index - 1] + t * (to[index] - to[index - 1]);
            }
        return to[^1] + (value - from[^1]);
    }

    private static List<double> Distinct(IEnumerable<double> values)
    {
        var levels = new List<double>();
        foreach (var value in values.OrderBy(value => value))
            if (levels.Count == 0 || value - levels[^1] > Tolerance) levels.Add(value);
        return levels;
    }

    public static string Title(VcfMemberRole role) => role switch
    {
        VcfMemberRole.TopChord => "Top chord",
        VcfMemberRole.MiddleChord => "Middle chord",
        VcfMemberRole.BottomChord => "Bottom chord",
        VcfMemberRole.VerticalChord => "Vertical chord",
        VcfMemberRole.MegaColumn => "Mega column",
        VcfMemberRole.EndFrame => "End frames",
        _ => "Cable",
    };
}
