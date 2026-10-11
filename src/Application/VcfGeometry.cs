using COT_VCF.Domain;
using COT_VCF.Geometry.Handlers;

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
    IReadOnlyDictionary<string, VcfMemberRole> Roles,
    IReadOnlyList<string> TopPoints,
    IReadOnlyList<string> MiddlePoints,
    IReadOnlyList<string> BottomPoints);

/// <summary>
/// A model with another number of bays (<see cref="VcfGeometry.ChangeBays"/>), still at the old bay width and levels.
/// <see cref="Templates"/> maps every regenerated member to the existing member it copies (section, Tab 03 settings);
/// <see cref="RenamedPoints"/> maps the old end-line chord points to their new names; <see cref="AddedCables"/> are new fan
/// cables with their template cable.
/// </summary>
public sealed record VcfTopology(
    GeometryModel Geometry,
    int Bays,
    IReadOnlyList<string> TopPoints,
    IReadOnlyList<string> MiddlePoints,
    IReadOnlyList<string> BottomPoints,
    IReadOnlyDictionary<string, string> Templates,
    IReadOnlyDictionary<string, string> RenamedPoints,
    IReadOnlyDictionary<string, string> AddedCables);

/// <summary>
/// Edit → VCF Geometry: reads the system from the model and rebuilds it from a few parameters. The topology and every
/// point / member name are kept — nodes are moved by a piecewise-linear map of their X (end-frame outer line, truss bay
/// lines) and Z (ground, back-stay level, bottom / middle / top chord), so Tabs 01–05 and the SAP loads still refer to the
/// same objects. Y is unchanged. Presentation-independent and never touches SAP2000.
/// </summary>
public static class VcfGeometry
{
    private const double Tolerance = 1e-3;
    private static readonly GeometryNamingService Naming = new();

    public static VcfGeometryAnalysis Analyze(GeometryModel geometry, TrussHeightConfiguration truss) =>
        Analyze(geometry, truss.TopPoints, truss.MiddlePoints, truss.BottomPoints);

    public static VcfGeometryAnalysis Analyze(VcfTopology topology) =>
        Analyze(topology.Geometry, topology.TopPoints, topology.MiddlePoints, topology.BottomPoints);

    public static VcfGeometryAnalysis Analyze(GeometryModel geometry, IReadOnlyList<string> topPoints, IReadOnlyList<string> middlePoints,
        IReadOnlyList<string> bottomPoints)
    {
        var truss = new TrussHeightConfiguration { TopPoints = topPoints, MiddlePoints = middlePoints, BottomPoints = bottomPoints };
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
            ground, lines, outerLeft, outerRight, hasMiddle, roles,
            truss.TopPoints.ToArray(), truss.MiddlePoints.ToArray(), truss.BottomPoints.ToArray());
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

    // ---- number of bays ----------------------------------------------------------------------------------------------

    /// <summary>The model with <paramref name="bays"/> bays. Bay lines are added or removed at the old bay width around the
    /// truss centre; chord points are named by position from the old pattern (TTrPt001 … TTrPt0nn); members tied to an end
    /// line (full-length chords, mega columns, back-stays, end frames) follow it; per-bay chord members and interior
    /// verticals are regenerated per bay / line; the fan cables keep their pattern (each top corner to the bottom-chord
    /// nodes up to mid-span). Existing names are kept where the position still exists; new names continue the numbering.
    /// Throws for a member the pattern does not cover.</summary>
    public static VcfTopology ChangeBays(VcfGeometryAnalysis analysis, int bays)
    {
        if (bays < 2 || bays > 200) throw new InvalidOperationException("Bays must be a whole number from 2 to 200.");
        var oldBays = analysis.Bays;
        var geometry = analysis.Geometry;
        var lines = analysis.TrussLines;
        var groups = new[] { analysis.TopPoints, analysis.MiddlePoints, analysis.BottomPoints };   // 0 top, 1 middle, 2 bottom
        var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var lineOf = new Dictionary<string, int>(StringComparer.Ordinal);
        int LineIndex(double x)
        {
            for (var index = 0; index < lines.Count; index++) if (Math.Abs(lines[index] - x) < Tolerance) return index;
            return -1;
        }
        var pattern = new (string Prefix, int First, int Width, double Y, double Z)?[3];
        for (var g = 0; g < 3; g++)
        {
            if (groups[g].Count == 0) continue;
            var byLine = new string?[lines.Count];
            foreach (var name in groups[g])
            {
                var line = LineIndex(geometry.Points[name].Position.X);
                if (line < 0 || byLine[line] is not null)
                    throw new InvalidOperationException("Changing the number of bays needs one chord point on every bay line for each chord.");
                byLine[line] = name; groupOf[name] = g; lineOf[name] = line;
            }
            if (byLine.Any(name => name is null))
                throw new InvalidOperationException("Changing the number of bays needs one chord point on every bay line for each chord.");
            var match = byLine.Select(name => System.Text.RegularExpressions.Regex.Match(name!, @"^(.*?)(\d+)$")).ToArray();
            if (match.Any(m => !m.Success) || match.Select(m => m.Groups[1].Value).Distinct().Count() != 1 ||
                Enumerable.Range(0, lines.Count).Any(i => int.Parse(match[i].Groups[2].Value) != int.Parse(match[0].Groups[2].Value) + i))
                throw new InvalidOperationException($"The chord point names ({byLine[0]} …) are not numbered along the truss, so new bays cannot be named.");
            var first = geometry.Points[byLine[0]!].Position;
            pattern[g] = (match[0].Groups[1].Value, int.Parse(match[0].Groups[2].Value), match[0].Groups[2].Value.Length, first.Y,
                groups[g].Select(name => geometry.Points[name].Position.Z).Average());
        }
        string PointName(int g, int line) => Naming.IncrementTrailingNumber(
            pattern[g]!.Value.Prefix + pattern[g]!.Value.First.ToString(new string('0', pattern[g]!.Value.Width)), line);

        var bay = analysis.Parameters.BayWidth;
        var centre = (lines[0] + lines[^1]) / 2;
        double LineX(int line) => centre + (line - bays / 2.0) * bay;
        var shiftLeft = LineX(0) - lines[0];
        var shiftRight = LineX(bays) - lines[^1];

        // points: the other points move with their end; the chord points are laid out again
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal);
        foreach (var (name, point) in geometry.Points)
        {
            if (groupOf.ContainsKey(name)) continue;
            var x = point.Position.X;
            double shift;
            if (x <= lines[0] + Tolerance) shift = shiftLeft;
            else if (x >= lines[^1] - Tolerance) shift = shiftRight;
            else throw new InvalidOperationException($"Point '{name}' lies inside the truss but is not a chord point; the number of bays cannot be changed.");
            points[name] = point with { Position = point.Position with { X = x + shift } };
        }
        for (var g = 0; g < 3; g++)
            if (pattern[g] is { } chord)
                for (var line = 0; line <= bays; line++)
                {
                    var name = PointName(g, line);
                    if (points.ContainsKey(name)) throw new InvalidOperationException($"Point name '{name}' is already used by another point.");
                    points[name] = new StructuralPoint(name, new Position(LineX(line), chord.Y, chord.Z));
                }
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var g = 0; g < 3; g++)
            if (pattern[g] is not null && PointName(g, oldBays) != PointName(g, bays)) renamed[PointName(g, oldBays)] = PointName(g, bays);
        string MapEnd(string name) => groupOf.TryGetValue(name, out var g)
            ? PointName(g, lineOf[name] == 0 ? 0 : bays)
            : name;

        // members
        var kept = new List<StructuralMember>();
        var perBay = new Dictionary<int, SortedDictionary<int, StructuralMember>>();               // group → bay → member
        var verticals = new Dictionary<(int Lower, int Upper), SortedDictionary<int, StructuralMember>>(); // groups → line → member
        var fans = new[] { new SortedDictionary<int, StructuralMember>(), new SortedDictionary<int, StructuralMember>() }; // left, right: distance → cable
        bool IsEnd(int line) => line == 0 || line == oldBays;
        foreach (var member in geometry.Members)
        {
            var aTruss = groupOf.TryGetValue(member.Start, out var ga);
            var bTruss = groupOf.TryGetValue(member.End, out var gb);
            var la = aTruss ? lineOf[member.Start] : -1;
            var lb = bTruss ? lineOf[member.End] : -1;
            if (!aTruss && !bTruss) { kept.Add(member); continue; }
            if (aTruss != bTruss)
            {
                if (IsEnd(aTruss ? la : lb)) { kept.Add(member with { Start = MapEnd(member.Start), End = MapEnd(member.End) }); continue; }
                throw Unsupported(member);
            }
            if (member.Kind == MemberKind.Frame && ga == gb && Math.Abs(la - lb) == oldBays) { kept.Add(member with { Start = MapEnd(member.Start), End = MapEnd(member.End) }); continue; }
            if (member.Kind == MemberKind.Frame && ga == gb && Math.Abs(la - lb) == 1)
            {
                if (!perBay.TryGetValue(ga, out var bays1)) perBay[ga] = bays1 = [];
                bays1[Math.Min(la, lb)] = member; continue;
            }
            if (member.Kind == MemberKind.Frame && la == lb && ga != gb)
            {
                if (IsEnd(la)) { kept.Add(member with { Start = MapEnd(member.Start), End = MapEnd(member.End) }); continue; }
                var key = (Math.Max(ga, gb), Math.Min(ga, gb));   // bottom = 2 is "lower"
                if (!verticals.TryGetValue(key, out var byLine)) verticals[key] = byLine = [];
                byLine[la] = member; continue;
            }
            if (member.Kind != MemberKind.Frame)
            {
                var (corner, other) = ga == 0 && IsEnd(la) && gb == 2 && !IsEnd(lb) ? (la, lb)
                    : gb == 0 && IsEnd(lb) && ga == 2 && !IsEnd(la) ? (lb, la) : (-1, -1);
                if (corner >= 0) { var side = corner == 0 ? 0 : 1; fans[side][Math.Abs(other - corner)] = member; continue; }
                if (IsEnd(la) && IsEnd(lb)) { kept.Add(member with { Start = MapEnd(member.Start), End = MapEnd(member.End) }); continue; }
            }
            throw Unsupported(member);
        }

        var used = new HashSet<string>(kept.Select(member => member.Name), StringComparer.Ordinal);
        var members = new List<StructuralMember>(kept);
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        string NextFrameName(StructuralMember template)
        {
            return Naming.NextSequentialName(template.Name, geometry.Members.Concat(members).Select(member => member.Name));
        }
        void Add(StructuralMember template, string start, string end, bool reuse)
        {
            var name = reuse && !used.Contains(template.Name) ? template.Name : NextFrameName(template);
            used.Add(name);
            members.Add(template with { Name = name, Start = start, End = end });
            templates[name] = template.Name;
        }
        foreach (var (g, byBay) in perBay)
            for (var b = 0; b < bays; b++)
            {
                var reuse = byBay.TryGetValue(b, out var existing);
                var template = existing ?? byBay[byBay.Keys.Where(key => key <= b).DefaultIfEmpty(byBay.Keys.First()).Max()];
                var forward = lineOf[template.Start] < lineOf[template.End];
                Add(template, PointName(g, forward ? b : b + 1), PointName(g, forward ? b + 1 : b), reuse);
            }
        foreach (var ((lower, upper), byLine) in verticals)
            for (var line = 1; line < bays; line++)
            {
                var reuse = byLine.TryGetValue(line, out var existing);
                var template = existing ?? byLine[byLine.Keys.Where(key => key <= line).DefaultIfEmpty(byLine.Keys.First()).Max()];
                var startLower = groupOf[template.Start] == lower;
                Add(template, PointName(startLower ? lower : upper, line), PointName(startLower ? upper : lower, line), reuse);
            }

        // fan cables: from each top corner to the bottom-chord nodes up to mid-span
        var added = new Dictionary<string, string>(StringComparer.Ordinal);
        var cableNumber = geometry.Members.Where(member => member.Kind != MemberKind.Frame)
            .Select(member => System.Text.RegularExpressions.Regex.Match(member.Name, @"^(.*?)(\d+)([abAB])(.*)$"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[2].Value)).DefaultIfEmpty(0).Max();
        // left fan: bottom lines 1 … ⌊n/2⌋; right fan: n−1 … ⌈n/2⌉ — ⌊n/2⌋ cables each (10 bays: 5 + 5, both reaching mid-span)
        var counts = new[] { bays / 2, bays / 2 };
        var extra = Math.Max(counts[0] - fans[0].Count, counts[1] - fans[1].Count);
        var newNumbers = Enumerable.Range(cableNumber + 1, Math.Max(0, extra)).ToArray();
        for (var side = 0; side < 2; side++)
        {
            if (fans[side].Count == 0) continue;
            var existing = fans[side].Values.ToArray();   // nearest first
            for (var k = 1; k <= counts[side]; k++)
            {
                var target = side == 0 ? k : bays - k;
                var corner = side == 0 ? 0 : bays;
                var template = existing[Math.Min(k, existing.Length) - 1];
                var topFirst = groupOf[template.Start] == 0;
                var (start, end) = topFirst ? (PointName(0, corner), PointName(2, target)) : (PointName(2, target), PointName(0, corner));
                string name;
                if (k <= existing.Length) name = existing[k - 1].Name;
                else
                {
                    name = Naming.SymmetricCableName(template.Name, newNumbers[k - existing.Length - 1], side == 0 ? 'A' : 'B');
                    if (used.Contains(name)) throw new InvalidOperationException($"Generated cable name '{name}' is already used.");
                    added[name] = template.Name;
                }
                used.Add(name);
                members.Add(template with { Name = name, Start = start, End = end });
                templates[name] = template.Name;
            }
        }

        string[] Group(int g) => pattern[g] is null ? [] : Enumerable.Range(0, bays + 1).Select(line => PointName(g, line)).ToArray();
        Naming.EnsureUnique(points.Keys);
        Naming.EnsureUnique(members.Select(member => member.Name));
        return new VcfTopology(new GeometryModel(points, members), bays, Group(0), Group(1), Group(2), templates, renamed, added);
    }

    private static InvalidOperationException Unsupported(StructuralMember member) =>
        new($"Member '{member.Name}' does not follow the VCF pattern (chords, verticals, mega columns, fan cables, end frames), so the number of bays cannot be changed.");

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
