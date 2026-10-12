using System.Globalization;

namespace Mcotis.Desktop.Application.Etabs;

/// <summary>
/// The inputs of Edit → Tied Arch System [m, kPa]: the arch rise <see cref="ArchRise"/> (H1, support → crown), the deck
/// height <see cref="DeckHeight"/> (H2, support → top chord), the number of bays and their spacing (span L = bays ×
/// spacing), and the storeys the deck carries: slab self-weight SW, superimposed dead load SDL and live load LL over a
/// tributary width B, on n floors.
/// </summary>
public sealed record TiedArchParameters(double ArchRise, double DeckHeight, int Bays, double BaySpacing,
    double TributaryWidth, double SelfWeight, double SuperimposedDead, double Live, int Floors)
{
    /// <summary>The TAS model (File → Load TAS…): H1 30 m, H2 33 m, 12 bays of 10 m, SW 7, SDL 3, LL 5 kPa, B 12 m, 10 floors.</summary>
    public static TiedArchParameters Default { get; } = new(30, 33, 12, 10, 12, 7, 3, 5, 10);

    // the loads of the storeys above, as offered by the form: min to max in steps
    public static IReadOnlyList<double> SelfWeightChoices { get; } = Steps(7, 15, 1);
    public static IReadOnlyList<double> SuperimposedDeadChoices { get; } = Steps(3, 10, 1);
    public static IReadOnlyList<double> LiveChoices { get; } = Steps(5, 10, 2.5);
    public static IReadOnlyList<int> FloorChoices { get; } = Enumerable.Range(5, 6).ToArray();

    public double Span => Bays * BaySpacing;
    /// <summary>The deck joint's load area per floor: bay spacing × tributary width [m²].</summary>
    public double TributaryArea => BaySpacing * TributaryWidth;
    /// <summary>SLS point load on an inner deck joint: (SW + SDL + LL) × s × B × n [kN].</summary>
    public double SlsPointLoad => (SelfWeight + SuperimposedDead + Live) * TributaryArea * Floors;
    /// <summary>ULS point load on an inner deck joint: (1.35 SW + 1.35 SDL + 1.5 LL) × s × B × n [kN].</summary>
    public double UlsPointLoad => (1.35 * (SelfWeight + SuperimposedDead) + 1.5 * Live) * TributaryArea * Floors;
    /// <summary>The point load of the dead pattern (DEAD_above: SW + SDL) and the live one (LL_above: LL) on an inner joint [kN].</summary>
    public double DeadPointLoad => (SelfWeight + SuperimposedDead) * TributaryArea * Floors;
    public double LivePointLoad => Live * TributaryArea * Floors;

    /// <summary>Height of the arch's parabola through the two supports and the crown at mid-span, x from the left support.</summary>
    public double ArchHeightAt(double x) => 4 * ArchRise * x * (Span - x) / (Span * Span);

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!(ArchRise > 0)) problems.Add("H1 (support → arch crown) must be greater than zero.");
        if (!(DeckHeight > ArchRise)) problems.Add("H2 (support → top chord) must be greater than H1: the deck sits above the arch crown.");
        if (Bays < 6 || Bays > 40 || Bays % 2 != 0)
            problems.Add("Number of bays: an even number from 6 to 40 (the crown is the joint at mid-span; three braced bays at each end).");
        if (!(BaySpacing > 0)) problems.Add("Bay spacing must be greater than zero.");
        if (!(TributaryWidth > 0)) problems.Add("Tributary width B must be greater than zero.");
        void Choice<T>(string name, T value, IReadOnlyList<T> choices, string unit)
        {
            if (!choices.Contains(value)) problems.Add($"{name}: choose {string.Join(", ", choices)} {unit}.".Replace("  ", " "));
        }
        Choice("SW", SelfWeight, SelfWeightChoices, "kPa");
        Choice("SDL", SuperimposedDead, SuperimposedDeadChoices, "kPa");
        Choice("LL", Live, LiveChoices, "kPa");
        Choice("Number of floors n", Floors, FloorChoices, "");
        return problems;
    }

    private static double[] Steps(double min, double max, double step) =>
        Enumerable.Range(0, (int)Math.Round((max - min) / step) + 1).Select(index => min + index * step).ToArray();
}

/// <summary>
/// Builds the tied arch system of Edit → Tied Arch System from <see cref="TiedArchParameters"/>, laid out as the TAS model
/// ETABS wrote (File → Load TAS…), whose materials, sections, load patterns, cases and combinations it keeps:
/// a parabolic arch rib from support to support through the crown (H1 at mid-span), mega columns at the supports up to
/// the deck (H2), a strut at every inner vertical line from the arch to the deck, and in the three end bays at each side a
/// horizontal brace at the arch's height at the second line, a diagonal below it and X braces above it. Struts and braces
/// are pinned at both ends; columns, arch and deck are continuous. The deck joints carry SW + SDL (DEAD_above) and LL
/// (LL_above; LL_left / LL_right on each half) over their tributary length — half a bay at the two end joints. X is 0 at
/// the first inner line, as in the ETABS model; joint names are label@story as ETABS names them.
/// </summary>
public static class TiedArchSystem
{
    private const string Column = "SRC MegaCol 1000x1200", Arch = "RSC 3.52x2.64 P", Deck = "CFT Deck 700x400x20",
        EndStrut = "CFT Strut-E 850x850x35", Strut = "CFT Strut 650x650x25", Brace = "CFT Brace 700x700x25";
    private static readonly string[] PinnedStart = ["M2I", "M3I"], PinnedEnd = ["M2J", "M3J"], Pinned = ["M2I", "M3I", "M2J", "M3J"];

    public static EtabsModel Build(EtabsModel template, TiedArchParameters p)
    {
        if (p.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        var missing = new[] { Column, Arch, Deck, EndStrut, Strut, Brace }.Where(name => template.Sections.All(section => section.Name != name)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"The TAS model is missing the section(s) {string.Join(", ", missing)}.");

        var n = p.Bays;
        var x0 = -p.BaySpacing;
        var braceLevel = p.ArchHeightAt(2 * p.BaySpacing);
        var joints = new Dictionary<string, EtabsJoint>(StringComparer.Ordinal);
        // a vertical line's label as in the ETABS model: 1 and 2 at the supports, 3 … at the inner lines
        static string Label(int line, int bays) => line == 0 ? "1" : line == bays ? "2" : (line + 2).ToString(CultureInfo.InvariantCulture);
        string Joint(int line, double z)
        {
            var story = z == 0 ? "Base" : "EL" + z.ToString("0.###", CultureInfo.InvariantCulture);
            var joint = new EtabsJoint(Label(line, n), story, x0 + line * p.BaySpacing, 0, z);
            joints.TryAdd(joint.Name, joint);
            return joint.Name;
        }
        string ArchJoint(int line) => Joint(line, line == 0 || line == n ? 0 : p.ArchHeightAt(line * p.BaySpacing));
        string DeckJoint(int line) => Joint(line, p.DeckHeight);
        string BraceJoint(int line) => line == 2 || line == n - 2 ? ArchJoint(line) : Joint(line, braceLevel);

        var frames = new List<EtabsFrame>();
        void Frame(string label, string kind, string start, string end, string section, IReadOnlyCollection<string>? releases = null) =>
            frames.Add(new EtabsFrame(label, joints[end].Story, kind, start, end, section,
                (releases ?? []).ToHashSet(StringComparer.Ordinal), 0, EtabsModel.NoModifiers));

        for (var bay = 1; bay <= n; bay++)
        {
            Frame($"ARCH{bay}", "BRACE", ArchJoint(bay - 1), ArchJoint(bay), Arch);
            Frame($"DECK{bay}", "BEAM", DeckJoint(bay - 1), DeckJoint(bay), Deck);
        }
        foreach (var (side, support) in new[] { ("L", 0), ("R", n) })
        {
            Frame($"COL{side}-1", "COLUMN", ArchJoint(support), BraceJoint(support), Column);
            Frame($"COL{side}-2", "COLUMN", BraceJoint(support), DeckJoint(support), Column);
        }
        for (var line = 1; line < n; line++)
        {
            if (line == 1 || line == n - 1)
            {
                Frame($"STR{line}-1", "COLUMN", ArchJoint(line), BraceJoint(line), EndStrut, PinnedStart);
                Frame($"STR{line}-2", "COLUMN", BraceJoint(line), DeckJoint(line), EndStrut, PinnedEnd);
            }
            else Frame($"STR{line}", "COLUMN", ArchJoint(line), DeckJoint(line), Strut, Pinned);
        }
        // the braced end bays, the right side the mirror of the left (line i ↔ n − i)
        foreach (var (side, at) in new (string, Func<int, int>)[] { ("L", line => line), ("R", line => n - line) })
        {
            Frame($"BH{side}-1", "BEAM", BraceJoint(at(0)), BraceJoint(at(1)), Brace, PinnedStart);
            Frame($"BH{side}-2", "BEAM", BraceJoint(at(1)), BraceJoint(at(2)), Brace, PinnedEnd);
            Frame($"BD{side}", "BRACE", ArchJoint(at(1)), BraceJoint(at(0)), Brace, Pinned);
            Frame($"BX{side}1", "BRACE", BraceJoint(at(0)), DeckJoint(at(1)), Brace, Pinned);
            Frame($"BX{side}2", "BRACE", BraceJoint(at(1)), DeckJoint(at(0)), Brace, Pinned);
            Frame($"BX{side}3", "BRACE", BraceJoint(at(1)), DeckJoint(at(2)), Brace, Pinned);
            Frame($"BX{side}4", "BRACE", BraceJoint(at(2)), DeckJoint(at(1)), Brace, Pinned);
            Frame($"BX{side}5", "BRACE", ArchJoint(at(2)), DeckJoint(at(3)), Brace, Pinned);
            Frame($"BX{side}6", "BRACE", ArchJoint(at(3)), DeckJoint(at(2)), Brace, Pinned);
        }

        var restraints = new[] { ArchJoint(0), ArchJoint(n) }.Select(joint => new EtabsRestraint(joint, [true, true, true, false, false, false])).ToArray();
        var loads = new List<EtabsJointLoad>();
        void Load(int line, string pattern, double share, double value)
        {
            var name = template.Patterns.FirstOrDefault(item => string.Equals(item.Name, pattern, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new InvalidOperationException($"The TAS model has no load pattern {pattern}.");
            if (share > 0) loads.Add(new EtabsJointLoad(DeckJoint(line), name, 0, 0, -share * value, 0, 0, 0));
        }
        for (var line = 0; line <= n; line++)
        {
            var tributary = line == 0 || line == n ? 0.5 : 1;    // half a bay at the two end joints
            Load(line, "DEAD_above", tributary, p.DeadPointLoad);
            Load(line, "LL_above", tributary, p.LivePointLoad);
            // live load on one half: the mid-span joint takes the half of its bay that lies on each side
            var left = line < n / 2 ? tributary : line == n / 2 ? 0.5 : 0;
            var right = line > n / 2 ? tributary : line == n / 2 ? 0.5 : 0;
            Load(line, "LL_left", left, p.LivePointLoad);
            Load(line, "LL_right", right, p.LivePointLoad);
        }
        return template with
        {
            Joints = joints.Values.ToArray(),
            Frames = frames,
            Restraints = restraints,
            JointLoads = loads,
            FrameLoads = [],
        };
    }
}
